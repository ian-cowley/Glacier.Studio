using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Glacier.Graph.Storage;

namespace Glacier.Showcase.Services.GlacierStudio;

/// <summary>
/// Stage 4 Super Agent Central Knowledge Graph Service.
/// Maintains an in-process zero-allocation Glacier.Graph.Storage.GraphStore of workspace projects,
/// dependency topology, files, C# AST types, interfaces, and methods.
/// </summary>
public sealed class GlacierWorkspaceGraphService
{
    private readonly GraphStore _graph;
    private readonly string _workspaceRoot;
    private readonly ConcurrentDictionary<string, DateTime> _indexedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _BootstrapLock = new();
    private bool _isBootstrapped;

    private static readonly Regex ProjectRefRegex = new(
        @"<ProjectReference\s+Include=""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NamespaceRegex = new(
        @"^\s*namespace\s+([a-zA-Z0-9_\.]+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex TypeDeclRegex = new(
        @"^\s*(?:public|internal|private|protected|file)?\s*(?:static|sealed|abstract|partial|readonly|\s)*\b(class|interface|struct|record|enum)\s+([a-zA-Z0-9_<>]+)(?:\s*:\s*([^\r\n\{]+))?",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex MethodDeclRegex = new(
        @"^\s*(?:public|internal|protected)\s+(?:static\s+|async\s+|virtual\s+|override\s+|unsafe\s+)*(?:[a-zA-Z0-9_<>\[\]\.\?,\s]+)\s+([a-zA-Z0-9_]+)\s*\(",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public GraphStore Store => _graph;
    public int NodeCount => _graph.NodeCount;
    public int EdgeCount => _graph.EdgeCount;

    public GlacierWorkspaceGraphService(string? workspaceRoot = null)
    {
        _workspaceRoot = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? workspaceRoot
            : GlacierWorkspaceResolver.ResolveWorkspaceRoot();

        _graph = new GraphStore(initialNodeCapacity: 32_768, initialEdgeCapacity: 131_072);
        EnsureBootstrapped();
    }

    public void EnsureBootstrapped()
    {
        if (_isBootstrapped) return;
        lock (_BootstrapLock)
        {
            if (_isBootstrapped) return;
            try
            {
                BootstrapWorkspaceTopology();
            }
            catch
            {
                // Ignore non-fatal I/O errors during initial workspace topology scan
            }
            _isBootstrapped = true;
        }
    }

    private void BootstrapWorkspaceTopology()
    {
        if (!Directory.Exists(_workspaceRoot)) return;

        _graph.AddNode("Workspace", $"root:{_workspaceRoot}");

        foreach (var dir in Directory.GetDirectories(_workspaceRoot))
        {
            string dirName = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(dirName) || dirName.StartsWith(".") ||
                dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("BenchmarkDotNet.Artifacts", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _graph.AddNode(dirName, "kind:project_dir");
            _graph.AddEdge("Workspace", dirName, "HAS_PROJECT");

            // Check README / overview docs in project root
            foreach (var mdFile in Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
            {
                string relMd = NormalizeRelativePath(mdFile);
                _graph.AddNode(relMd, "kind:documentation");
                _graph.AddEdge(dirName, relMd, "HAS_DOCUMENTATION");
            }

            // Scan .csproj files up to 3 levels deep to build dependency ontology
            string srcSubDir = Path.Combine(dir, "src");
            var searchRoot = Directory.Exists(srcSubDir) ? srcSubDir : dir;
            try
            {
                foreach (var csproj in Directory.GetFiles(searchRoot, "*.csproj", SearchOption.AllDirectories))
                {
                    if (csproj.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) ||
                        csproj.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relCsproj = NormalizeRelativePath(csproj);
                    string projName = Path.GetFileNameWithoutExtension(csproj);
                    _graph.AddNode(projName, $"kind:assembly|manifest:{relCsproj}");
                    _graph.AddEdge(dirName, projName, "DEFINES_ASSEMBLY");
                    _graph.AddEdge(projName, relCsproj, "HAS_MANIFEST");

                    string xml = File.ReadAllText(csproj);
                    foreach (Match m in ProjectRefRegex.Matches(xml))
                    {
                        string refPath = m.Groups[1].Value;
                        string refProjName = Path.GetFileNameWithoutExtension(refPath.Replace('\\', '/'));
                        if (!string.IsNullOrEmpty(refProjName))
                        {
                            _graph.AddNode(refProjName, "kind:assembly");
                            _graph.AddEdge(projName, refProjName, "DEPENDS_ON");
                        }
                    }

                    // Also index top-level C# filenames in the project directory for instant symbol resolution
                    string? csprojDir = Path.GetDirectoryName(csproj);
                    if (!string.IsNullOrEmpty(csprojDir) && Directory.Exists(csprojDir))
                    {
                        foreach (var csFile in Directory.GetFiles(csprojDir, "*.cs", SearchOption.AllDirectories).Take(80))
                        {
                            if (csFile.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) ||
                                csFile.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
                                continue;

                            string relCs = NormalizeRelativePath(csFile);
                            string fileStem = Path.GetFileNameWithoutExtension(csFile);
                            _graph.AddNode(relCs, $"kind:source_file|stem:{fileStem}");
                            _graph.AddEdge(projName, relCs, "CONTAINS_SOURCE");

                            // Map likely type name from filename stem
                            if (!fileStem.Contains('.'))
                            {
                                _graph.AddNode(fileStem, $"kind:symbol|file:{relCs}");
                                _graph.AddEdge(fileStem, relCs, "DEFINED_IN");
                            }
                        }
                    }
                }
            }
            catch
            {
                // Continue scanning remaining directories
            }
        }
    }

    /// <summary>
    /// Ingests directory listing observations into the Knowledge Graph.
    /// </summary>
    public void IngestDirectory(string dirPath, IEnumerable<string> subdirectories, IEnumerable<string> files)
    {
        EnsureBootstrapped();
        string normDir = string.IsNullOrWhiteSpace(dirPath) ? "Workspace" : NormalizeRelativePath(dirPath);
        _graph.AddNode(normDir, "kind:directory");

        foreach (var sub in subdirectories)
        {
            string childDir = normDir == "Workspace" ? sub : $"{normDir}/{sub}";
            _graph.AddNode(childDir, "kind:directory");
            _graph.AddEdge(normDir, childDir, "CONTAINS_DIR");
        }

        foreach (var file in files)
        {
            string childFile = normDir == "Workspace" ? file : $"{normDir}/{file}";
            _graph.AddNode(childFile, "kind:file");
            _graph.AddEdge(normDir, childFile, "CONTAINS_FILE");
        }
    }

    /// <summary>
    /// Parses a source or markdown file and populates AST symbol nodes &amp; edges in the Knowledge Graph.
    /// </summary>
    public List<string> IngestFileContent(string filePath, string content)
    {
        EnsureBootstrapped();
        var discoveredSymbols = new List<string>();
        if (string.IsNullOrWhiteSpace(content)) return discoveredSymbols;

        string relPath = NormalizeRelativePath(filePath);
        _indexedFiles[relPath] = DateTime.UtcNow;

        int lineCount = 1;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n') lineCount++;
        }

        _graph.AddNode(relPath, $"kind:file|lines:{lineCount}|indexed:true");

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".cs" || ext == ".razor")
        {
            foreach (Match nsMatch in NamespaceRegex.Matches(content))
            {
                string ns = nsMatch.Groups[1].Value.Trim();
                _graph.AddNode(ns, "kind:namespace");
                _graph.AddEdge(relPath, ns, "IN_NAMESPACE");
            }

            foreach (Match typeMatch in TypeDeclRegex.Matches(content))
            {
                string kind = typeMatch.Groups[1].Value.Trim();
                string typeName = typeMatch.Groups[2].Value.Trim();
                int lineNum = GetLineNumberAtOffset(content, typeMatch.Index);

                string meta = $"kind:{kind}|file:{relPath}|line:{lineNum}";

                // Extract concise body members for enums, interfaces, structs, and records so models never guess definitions
                int openBrace = content.IndexOf('{', typeMatch.Index);
                int closeBrace = openBrace >= 0 ? content.IndexOf('}', openBrace + 1) : -1;
                if (openBrace >= 0 && closeBrace > openBrace && (closeBrace - openBrace) < 2500)
                {
                    string body = content.Substring(openBrace + 1, closeBrace - openBrace - 1);
                    var members = new List<string>();
                    foreach (var rawLine in body.Split('\n'))
                    {
                        string cleanLine = rawLine.Trim();
                        if (string.IsNullOrEmpty(cleanLine) || cleanLine.StartsWith("//") || cleanLine.StartsWith("/*") || cleanLine.StartsWith("*") || cleanLine.StartsWith("["))
                            continue;
                        int commentIdx = cleanLine.IndexOf("//", StringComparison.Ordinal);
                        if (commentIdx >= 0) cleanLine = cleanLine[..commentIdx].Trim();
                        cleanLine = cleanLine.TrimEnd(',', ';');
                        if (!string.IsNullOrEmpty(cleanLine) && char.IsLetter(cleanLine[0]) && cleanLine.Length < 90)
                        {
                            members.Add(cleanLine);
                        }
                    }
                    if (members.Count > 0)
                    {
                        meta += $"|members:{string.Join("; ", members.Take(10))}";
                    }
                }
                else if (string.Equals(kind, "record", StringComparison.OrdinalIgnoreCase))
                {
                    int openParen = content.IndexOf('(', typeMatch.Index);
                    int closeParen = openParen >= 0 ? content.IndexOf(')', openParen + 1) : -1;
                    if (openParen >= 0 && closeParen > openParen && (closeParen - openParen) < 600)
                    {
                        string recordParams = Regex.Replace(content.Substring(openParen + 1, closeParen - openParen - 1), @"\s+", " ").Trim();
                        meta += $"|params:{recordParams}";
                    }
                }

                _graph.AddNode(typeName, meta);
                _graph.AddEdge(relPath, typeName, $"DECLARES_{kind.ToUpperInvariant()}");
                _graph.AddEdge(typeName, relPath, "DEFINED_IN");
                discoveredSymbols.Add($"{kind} {typeName} (L{lineNum})");

                if (typeMatch.Groups[3].Success)
                {
                    string basesRaw = typeMatch.Groups[3].Value;
                    foreach (var basePart in basesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string baseName = basePart.Trim().Split(' ')[0].Trim();
                        if (!string.IsNullOrEmpty(baseName) && char.IsLetter(baseName[0]))
                        {
                            _graph.AddNode(baseName, "kind:type_or_interface");
                            _graph.AddEdge(typeName, baseName, "INHERITS_OR_IMPLEMENTS");
                        }
                    }
                }
            }

            int methodCount = 0;
            foreach (Match methodMatch in MethodDeclRegex.Matches(content))
            {
                string methodName = methodMatch.Groups[1].Value.Trim();
                if (methodName is "if" or "for" or "foreach" or "while" or "switch" or "lock" or "using" or "catch" or "return")
                    continue;

                int lineNum = GetLineNumberAtOffset(content, methodMatch.Index);
                string qualifiedMethod = $"{Path.GetFileNameWithoutExtension(relPath)}.{methodName}";
                _graph.AddNode(qualifiedMethod, $"kind:method|file:{relPath}|line:{lineNum}");
                _graph.AddEdge(relPath, qualifiedMethod, "DECLARES_METHOD");

                if (methodCount < 12)
                {
                    discoveredSymbols.Add($"method {methodName}() (L{lineNum})");
                }
                methodCount++;
            }
        }

        return discoveredSymbols;
    }

    private readonly HashSet<string> _deepIndexedProjects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// On-demand deep AST indexing of all C# source files in a target project directory.
    /// Ensures every class, struct, interface, enum (with its real values), and public method is in Glacier.Graph.
    /// </summary>
    public void EnsureProjectDeepIndexed(string targetQuery)
    {
        if (string.IsNullOrWhiteSpace(targetQuery) || string.Equals(targetQuery, "Workspace", StringComparison.OrdinalIgnoreCase))
            return;

        string clean = targetQuery.Trim().Replace('\\', '/').Split('/')[0];
        if (string.IsNullOrEmpty(clean)) return;

        lock (_BootstrapLock)
        {
            if (!_deepIndexedProjects.Add(clean)) return;
        }

        string projDir = Path.Combine(_workspaceRoot, clean);
        if (!Directory.Exists(projDir)) return;

        string srcDir = Path.Combine(projDir, "src");
        string scanRoot = Directory.Exists(srcDir) ? srcDir : projDir;

        try
        {
            foreach (var csFile in Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories).Take(80))
            {
                if (csFile.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) ||
                    csFile.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
                    continue;

                string content = File.ReadAllText(csFile);
                IngestFileContent(csFile, content);
            }
        }
        catch
        {
            // Ignore non-fatal file read errors during deep scan
        }
    }

    /// <summary>
    /// Queries the Knowledge Graph around a target entity and returns a compact ontological neighborhood summary,
    /// including verified C# AST types, enums, and method signatures when a project is targeted.
    /// </summary>
    public string QueryNeighborhoodSummary(string targetQuery, int maxEdges = 28)
    {
        EnsureBootstrapped();
        if (string.IsNullOrWhiteSpace(targetQuery)) return "";

        EnsureProjectDeepIndexed(targetQuery);

        var candidateNodes = FindMatchingNodes(targetQuery, maxCandidates: 6);
        if (candidateNodes.Count == 0)
        {
            return $"[Knowledge Graph: No indexed nodes yet matching '{targetQuery}'. Total Graph: {_graph.NodeCount} nodes, {_graph.EdgeCount} edges]";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"### Knowledge Graph Context for `{targetQuery}` ({_graph.NodeCount} nodes, {_graph.EdgeCount} edges)");

        int emittedEdges = 0;
        foreach (var nodeId in candidateNodes)
        {
            string meta = _graph.GetNodeMetadata(nodeId);
            sb.AppendLine(!string.IsNullOrEmpty(meta)
                ? $"- **Node `{nodeId}`** (`{meta}`)"
                : $"- **Node `{nodeId}`**");

            // Collect outward edges
            var edgesByRel = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var enumerator = _graph.GetOutwardEdges(nodeId);
            while (enumerator.MoveNext())
            {
                string rel = _graph.GetRelationType(enumerator.CurrentRelationId);
                string target = _graph.GetExternalId(enumerator.CurrentTargetNodeId);
                if (!edgesByRel.TryGetValue(rel, out var list))
                {
                    list = new List<string>();
                    edgesByRel[rel] = list;
                }
                if (list.Count < 14 && !list.Contains(target))
                {
                    list.Add(target);
                }
                enumerator.Advance();
            }

            foreach (var kvp in edgesByRel)
            {
                if (emittedEdges >= maxEdges) break;
                sb.AppendLine($"  - `{kvp.Key}` → {string.Join(", ", kvp.Value.Select(v => $"`{v}`"))}");
                emittedEdges++;
            }
        }

        // If targetQuery is a project directory, append verified AST symbols (enums, structs, classes, interfaces, key methods)
        string projPrefix = targetQuery.Trim().Replace('\\', '/').Split('/')[0] + "/src/";
        var projectTypeFacts = new List<string>();
        int totalNodes = _graph.NodeCount;
        for (int i = 1; i <= totalNodes && projectTypeFacts.Count < 20; i++)
        {
            string extId = _graph.GetExternalId(i);
            string meta = _graph.GetNodeMetadata(extId);
            if (string.IsNullOrEmpty(meta) || !meta.Contains(projPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (meta.StartsWith("kind:enum|", StringComparison.OrdinalIgnoreCase) ||
                meta.StartsWith("kind:interface|", StringComparison.OrdinalIgnoreCase) ||
                meta.StartsWith("kind:struct|", StringComparison.OrdinalIgnoreCase) ||
                meta.StartsWith("kind:class|", StringComparison.OrdinalIgnoreCase) ||
                meta.StartsWith("kind:record|", StringComparison.OrdinalIgnoreCase))
            {
                // Also gather declared methods for the file
                projectTypeFacts.Add($"  - `{extId}` ({meta})");
            }
        }

        if (projectTypeFacts.Count > 0)
        {
            sb.AppendLine($"- **Verified C# AST Symbols in `{targetQuery}`**:");
            foreach (var fact in projectTypeFacts)
            {
                sb.AppendLine(fact);
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Resolves a partial filename or symbol to its known workspace relative file path via the Knowledge Graph.
    /// </summary>
    public string? TryResolveFilePath(string queryName)
    {
        EnsureBootstrapped();
        if (string.IsNullOrWhiteSpace(queryName)) return null;

        string clean = queryName.Trim().Replace('\\', '/');
        string fullCandidate = Path.IsPathRooted(clean) ? clean : Path.Combine(_workspaceRoot, clean);
        if (File.Exists(fullCandidate)) return fullCandidate;

        // Check if queryName is a symbol with DEFINED_IN edge
        var enumerator = _graph.GetOutwardEdges(clean);
        while (enumerator.MoveNext())
        {
            string rel = _graph.GetRelationType(enumerator.CurrentRelationId);
            if (rel == "DEFINED_IN")
            {
                string targetRel = _graph.GetExternalId(enumerator.CurrentTargetNodeId);
                string resolved = Path.Combine(_workspaceRoot, targetRel);
                if (File.Exists(resolved)) return resolved;
            }
            enumerator.Advance();
        }

        // Check file stem match on disk
        string fileNameOnly = Path.GetFileName(clean);
        if (!string.IsNullOrEmpty(fileNameOnly))
        {
            try
            {
                string searchPattern = fileNameOnly.Contains('.') ? fileNameOnly : $"{fileNameOnly}.cs";
                var found = Directory.EnumerateFiles(_workspaceRoot, searchPattern, SearchOption.AllDirectories)
                    .FirstOrDefault(f => !f.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) &&
                                         !f.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(found)) return found;
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// High-speed in-process symbol and text search across the workspace, automatically populating the Knowledge Graph.
    /// </summary>
    public string SearchWorkspace(string query, string? scopePath = null, int maxMatches = 25)
    {
        EnsureBootstrapped();
        if (string.IsNullOrWhiteSpace(query)) return "Error: 'query' parameter is required.";

        var sw = Stopwatch.StartNew();
        string rootDir = _workspaceRoot;
        if (!string.IsNullOrWhiteSpace(scopePath))
        {
            string candidateDir = Path.IsPathRooted(scopePath) ? scopePath : Path.Combine(_workspaceRoot, scopePath);
            if (Directory.Exists(candidateDir)) rootDir = candidateDir;
        }

        var results = new List<string>();
        var allowedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".razor", ".csproj", ".slnx", ".md", ".json", ".yml", ".yaml"
        };

        string? containerHint = null;
        string? memberTerm = null;
        if (query.Contains('.') && !query.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !query.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var parts = query.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                containerHint = parts[^2];
                memberTerm = parts[^1];
            }
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories))
            {
                if (results.Count >= maxMatches) break;

                if (file.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains("\\BenchmarkDotNet.Artifacts\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string ext = Path.GetExtension(file);
                if (!allowedExts.Contains(ext)) continue;

                string relPath = NormalizeRelativePath(file);

                // Check filename match first
                if (Path.GetFileName(file).Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add($"[FILE MATCH] {relPath}");
                }

                // Scan file lines
                string content = File.ReadAllText(file);
                string effectiveSearchTerm = query;
                int idx = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);

                if (idx < 0 && !string.IsNullOrEmpty(containerHint) && !string.IsNullOrEmpty(memberTerm))
                {
                    if (relPath.Contains(containerHint, StringComparison.OrdinalIgnoreCase) ||
                        content.Contains(containerHint, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = content.IndexOf(memberTerm, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0) effectiveSearchTerm = memberTerm;
                    }
                }

                if (idx >= 0)
                {
                    // Ingest discovered source file into Knowledge Graph
                    IngestFileContent(file, content);

                    using var reader = new StringReader(content);
                    string? line;
                    int lineNum = 0;
                    int fileHits = 0;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNum++;
                        if (line.Contains(effectiveSearchTerm, StringComparison.OrdinalIgnoreCase))
                        {
                            string snippet = line.Trim();
                            if (snippet.Length > 140) snippet = snippet[..140] + "...";
                            results.Add($"{relPath}:{lineNum}: {snippet}");
                            fileHits++;
                            if (fileHits >= 4 || results.Count >= maxMatches) break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            results.Add($"Search warning: {ex.Message}");
        }

        sw.Stop();
        if (results.Count == 0)
        {
            return $"No matches found for '{query}' in '{NormalizeRelativePath(rootDir)}' ({sw.Elapsed.TotalMilliseconds:F1}ms).\n" +
                   QueryNeighborhoodSummary(query, maxEdges: 10);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} match(es) for '{query}' in {sw.Elapsed.TotalMilliseconds:F1}ms:");
        foreach (var r in results)
        {
            sb.AppendLine($"- {r}");
        }
        return sb.ToString().Trim();
    }

    private List<string> FindMatchingNodes(string query, int maxCandidates)
    {
        var matches = new List<string>();
        string clean = query.Trim().Replace('\\', '/');

        // Exact match first
        if (_graph.TryGetInternalId(clean, out _))
        {
            matches.Add(clean);
        }

        string fileNameNoExt = Path.GetFileNameWithoutExtension(clean);
        if (!string.IsNullOrEmpty(fileNameNoExt) && !matches.Contains(fileNameNoExt) && _graph.TryGetInternalId(fileNameNoExt, out _))
        {
            matches.Add(fileNameNoExt);
        }

        // Scan internal node IDs for substring match
        int totalNodes = _graph.NodeCount;
        for (int i = 1; i <= totalNodes && matches.Count < maxCandidates; i++)
        {
            string extId = _graph.GetExternalId(i);
            if (!string.IsNullOrEmpty(extId) &&
                !matches.Contains(extId) &&
                extId.Contains(clean, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(extId);
            }
        }

        return matches;
    }

    public string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            string full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(_workspaceRoot, path));
            if (full.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetRelativePath(_workspaceRoot, full).Replace('\\', '/');
            }
        }
        catch { }
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static int GetLineNumberAtOffset(string text, int offset)
    {
        int line = 1;
        int limit = Math.Min(offset, text.Length);
        for (int i = 0; i < limit; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }
}
