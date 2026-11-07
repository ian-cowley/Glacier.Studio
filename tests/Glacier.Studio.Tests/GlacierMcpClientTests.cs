namespace Glacier.Studio.Tests;

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

[Trait("Category", "Unit")]
public class GlacierMcpClientTests
{
    [Fact]
    public async Task GlacierMcpClient_InitializeAsync_ExecutesHandshakeSuccessfully()
    {
        var (clientIn, clientOut, serverIn, serverOut) = CreateMemoryStreamPair();

        await using var client = new GlacierMcpClient("test-server", clientIn, clientOut);

        // Run mock MCP server in background
        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(serverIn, Encoding.UTF8);
            using var writer = new StreamWriter(serverOut, Encoding.UTF8) { AutoFlush = true };

            // 1. Read initialize request
            string? line1 = await reader.ReadLineAsync();
            Assert.NotNull(line1);
            using var doc1 = JsonDocument.Parse(line1);
            long id = doc1.RootElement.GetProperty("id").GetInt64();
            Assert.Equal("initialize", doc1.RootElement.GetProperty("method").GetString());

            // 2. Respond to initialize
            var initResponse = new
            {
                jsonrpc = "2.0",
                id = id,
                result = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "mock-mcp-server", version = "1.0.0" }
                }
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(initResponse));

            // 3. Read notifications/initialized
            string? line2 = await reader.ReadLineAsync();
            Assert.NotNull(line2);
            using var doc2 = JsonDocument.Parse(line2);
            Assert.Equal("notifications/initialized", doc2.RootElement.GetProperty("method").GetString());
        });

        await client.InitializeAsync();
        await serverTask;

        Assert.True(client.IsInitialized);
        Assert.True(client.IsRunning);
        Assert.Equal("test-server", client.ServerName);
    }

    [Fact]
    public async Task GlacierMcpClient_ListToolsAsync_DiscoversToolsAndSchemas()
    {
        var (clientIn, clientOut, serverIn, serverOut) = CreateMemoryStreamPair();
        await using var client = new GlacierMcpClient("tools-server", clientIn, clientOut);

        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(serverIn, Encoding.UTF8);
            using var writer = new StreamWriter(serverOut, Encoding.UTF8) { AutoFlush = true };

            // Handle initialize
            string? initLine = await reader.ReadLineAsync();
            using var initDoc = JsonDocument.Parse(initLine!);
            long initId = initDoc.RootElement.GetProperty("id").GetInt64();
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = initId,
                result = new { protocolVersion = "2024-11-05", capabilities = new { } }
            }));

            // Read initialized notification
            await reader.ReadLineAsync();

            // Handle tools/list
            string? listLine = await reader.ReadLineAsync();
            using var listDoc = JsonDocument.Parse(listLine!);
            long listId = listDoc.RootElement.GetProperty("id").GetInt64();
            Assert.Equal("tools/list", listDoc.RootElement.GetProperty("method").GetString());

            var listResponse = new
            {
                jsonrpc = "2.0",
                id = listId,
                result = new
                {
                    tools = new object[]
                    {
                        new
                        {
                            name = "vector_search",
                            description = "Performs cosine similarity search over vector embeddings",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new { query = new { type = "string" } },
                                required = new[] { "query" }
                            }
                        },
                        new
                        {
                            name = "benchmark_eval",
                            description = "Executes hardware benchmark evaluation",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new { iterations = new { type = "integer" } }
                            }
                        }
                    }
                }
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(listResponse));
        });

        var tools = await client.ListToolsAsync();
        await serverTask;

        Assert.NotNull(tools);
        Assert.Equal(2, tools.Count);

        var tool1 = tools[0];
        Assert.Equal("vector_search", tool1.Name);
        Assert.Equal("Performs cosine similarity search over vector embeddings", tool1.Description);
        Assert.Equal(JsonValueKind.Object, tool1.InputSchema.ValueKind);

        var tool2 = tools[1];
        Assert.Equal("benchmark_eval", tool2.Name);
    }

    [Fact]
    public async Task GlacierMcpClient_CallToolAsync_SerializesArgumentsAndReturnsText()
    {
        var (clientIn, clientOut, serverIn, serverOut) = CreateMemoryStreamPair();
        await using var client = new GlacierMcpClient("calc-server", clientIn, clientOut);

        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(serverIn, Encoding.UTF8);
            using var writer = new StreamWriter(serverOut, Encoding.UTF8) { AutoFlush = true };

            // Handle initialize
            string? initLine = await reader.ReadLineAsync();
            using var initDoc = JsonDocument.Parse(initLine!);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = initDoc.RootElement.GetProperty("id").GetInt64(),
                result = new { protocolVersion = "2024-11-05" }
            }));
            await reader.ReadLineAsync(); // initialized notification

            // Handle tools/call
            string? callLine = await reader.ReadLineAsync();
            using var callDoc = JsonDocument.Parse(callLine!);
            long callId = callDoc.RootElement.GetProperty("id").GetInt64();
            Assert.Equal("tools/call", callDoc.RootElement.GetProperty("method").GetString());

            var @params = callDoc.RootElement.GetProperty("params");
            Assert.Equal("calculate_norm", @params.GetProperty("name").GetString());
            var args = @params.GetProperty("arguments");
            Assert.Equal(3, args.GetProperty("dim").GetInt32());

            var callResponse = new
            {
                jsonrpc = "2.0",
                id = callId,
                result = new
                {
                    content = new[]
                    {
                        new { type = "text", text = "L2 Norm = 5.0" }
                    },
                    isError = false
                }
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(callResponse));
        });

        string result = await client.CallToolAsync("calculate_norm", "{\"dim\": 3}");
        await serverTask;

        Assert.Equal("L2 Norm = 5.0", result);
    }

    [Fact]
    public async Task GlacierMcpClient_ErrorResponse_ThrowsMcpRpcException()
    {
        var (clientIn, clientOut, serverIn, serverOut) = CreateMemoryStreamPair();
        await using var client = new GlacierMcpClient("error-server", clientIn, clientOut);

        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(serverIn, Encoding.UTF8);
            using var writer = new StreamWriter(serverOut, Encoding.UTF8) { AutoFlush = true };

            string? line = await reader.ReadLineAsync();
            using var doc = JsonDocument.Parse(line!);
            long id = doc.RootElement.GetProperty("id").GetInt64();

            var errorResponse = new
            {
                jsonrpc = "2.0",
                id = id,
                error = new
                {
                    code = -32601,
                    message = "Method 'unknown_method' not found."
                }
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(errorResponse));
        });

        await client.StartAsync();

        var ex = await Assert.ThrowsAsync<McpRpcException>(() =>
            client.SendRequestAsync("unknown_method", new { }));
        await serverTask;

        Assert.Equal(-32601, ex.Code);
        Assert.Contains("Method 'unknown_method' not found", ex.Message);
    }

    [Fact]
    public async Task GlacierMcpClient_Framing_IgnoresDiagnosticsAndNonJsonLines()
    {
        var (clientIn, clientOut, serverIn, serverOut) = CreateMemoryStreamPair();
        await using var client = new GlacierMcpClient("diagnostic-server", clientIn, clientOut);

        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(serverIn, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(serverOut, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            string? reqLine = await reader.ReadLineAsync();
            using var doc = JsonDocument.Parse(reqLine!);
            long id = doc.RootElement.GetProperty("id").GetInt64();

            // Write diagnostic stderr/stdout logs before JSON-RPC payload
            await writer.WriteLineAsync("[DEBUG] Initializing native .NET 10 runtime modules...");
            await writer.WriteLineAsync("[INFO] Server listening on standard I/O");
            await writer.WriteLineAsync(""); // empty line

            var validResponse = new
            {
                jsonrpc = "2.0",
                id = id,
                result = new { status = "active" }
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(validResponse));
        });

        await client.StartAsync();
        var result = await client.SendRequestAsync("ping", null);
        await serverTask;

        Assert.Equal("active", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GlacierMcpClient_SendNotificationAsync_SendsWithoutExpectingResponse()
    {
        var (clientIn, clientOut, serverIn, serverOut) = CreateMemoryStreamPair();
        await using var client = new GlacierMcpClient("notify-server", clientIn, clientOut);

        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(serverIn, Encoding.UTF8);
            string? line = await reader.ReadLineAsync();
            Assert.NotNull(line);
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal("custom/event", doc.RootElement.GetProperty("method").GetString());
            Assert.False(doc.RootElement.TryGetProperty("id", out _), "Notifications must not contain an id property.");
            Assert.Equal("sample_payload", doc.RootElement.GetProperty("params").GetProperty("data").GetString());
        });

        await client.StartAsync();
        await client.SendNotificationAsync("custom/event", new { data = "sample_payload" });
        await serverTask;
    }

    [Fact]
    public async Task GlacierMcpClient_OverAnonymousPipeStream_ExecutesSuccessfully()
    {
        // Interop test with actual System.IO.Pipes.PipeStream (AnonymousPipeServerStream / AnonymousPipeClientStream)
        using var clientToServerServer = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        using var clientToServerClient = new AnonymousPipeClientStream(PipeDirection.Out, clientToServerServer.GetClientHandleAsString());

        using var serverToClientServer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var serverToClientClient = new AnonymousPipeClientStream(PipeDirection.In, serverToClientServer.GetClientHandleAsString());

        await using var client = new GlacierMcpClient("pipe-server", serverToClientClient, clientToServerClient);

        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(clientToServerServer, Encoding.UTF8);
            using var writer = new StreamWriter(serverToClientServer, Encoding.UTF8) { AutoFlush = true };

            string? line = await reader.ReadLineAsync();
            using var doc = JsonDocument.Parse(line!);
            long id = doc.RootElement.GetProperty("id").GetInt64();

            var response = new
            {
                jsonrpc = "2.0",
                id = id,
                result = new { pipeSuccess = true }
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(response));
        });

        await client.StartAsync();
        var result = await client.SendRequestAsync("test_pipe", null);
        await serverTask;

        Assert.True(result.GetProperty("pipeSuccess").GetBoolean());
    }

    [Fact]
    public void GlacierMcpClient_Dispose_CleansUpRunningState()
    {
        var (clientIn, clientOut, _, _) = CreateMemoryStreamPair();
        var client = new GlacierMcpClient("dispose-server", clientIn, clientOut);

        Assert.False(client.IsRunning);
        client.StartAsync();
        Assert.True(client.IsRunning);

        client.Dispose();
        Assert.False(client.IsRunning);

        Assert.Throws<ObjectDisposedException>(() =>
        {
            client.StartAsync().GetAwaiter().GetResult();
        });
    }

    private static (Stream ClientIn, Stream ClientOut, Stream ServerIn, Stream ServerOut) CreateMemoryStreamPair()
    {
        var clientToServer = new MemoryChannelStream();
        var serverToClient = new MemoryChannelStream();

        return (
            ClientIn: serverToClient,
            ClientOut: clientToServer,
            ServerIn: clientToServer,
            ServerOut: serverToClient
        );
    }

    /// <summary>
    /// Thread-safe in-memory stream backed by an unbounded async channel, providing full duplex I/O with zero subprocess overhead.
    /// </summary>
    private sealed class MemoryChannelStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private byte[]? _currentChunk;
        private int _chunkOffset;
        private bool _isDisposed;

        public override bool CanRead => !_isDisposed;
        public override bool CanWrite => !_isDisposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (_currentChunk == null || _chunkOffset >= _currentChunk.Length)
            {
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0; // End of stream
                }

                if (_channel.Reader.TryRead(out var chunk))
                {
                    _currentChunk = chunk;
                    _chunkOffset = 0;
                }
            }

            int toCopy = Math.Min(count, _currentChunk.Length - _chunkOffset);
            Buffer.BlockCopy(_currentChunk, _chunkOffset, buffer, offset, toCopy);
            _chunkOffset += toCopy;
            return toCopy;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_currentChunk == null || _chunkOffset >= _currentChunk.Length)
            {
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                if (_channel.Reader.TryRead(out var chunk))
                {
                    _currentChunk = chunk;
                    _chunkOffset = 0;
                }
            }

            int toCopy = Math.Min(buffer.Length, _currentChunk.Length - _chunkOffset);
            _currentChunk.AsSpan(_chunkOffset, toCopy).CopyTo(buffer.Span);
            _chunkOffset += toCopy;
            return toCopy;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            byte[] copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            _channel.Writer.TryWrite(copy);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            byte[] copy = buffer.ToArray();
            _channel.Writer.TryWrite(copy);
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _channel.Writer.TryComplete();
            }
            base.Dispose(disposing);
        }
    }
}
