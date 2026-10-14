using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Glacier.DocTree.Core;
using Glacier.DocTree.Parser;

namespace Glacier.Showcase.Services.GlacierStudio;

public class GlacierDocTreePreviewService
{
    private DocumentPreviewModel _activeDocument = new();
    private readonly string _workspaceRoot;

    public DocumentPreviewModel ActiveDocument => _activeDocument;
    public event Action? OnDocumentChanged;

    public GlacierDocTreePreviewService(string? workspaceRoot = null)
    {
        _workspaceRoot = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? workspaceRoot
            : GlacierWorkspaceResolver.ResolveWorkspaceRoot();

        // Load default document
        string defaultPath = Path.Combine(_workspaceRoot, "README.md");
        if (File.Exists(defaultPath))
        {
            LoadFile(defaultPath);
        }
        else
        {
            SetDocument("Welcome to Glacier Agent Studio", "# ❄️ Glacier Agent Studio\n\nWelcome to your autonomous AI development cockpit.", "welcome.md");
        }
    }

    public void LoadFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                string combined = Path.Combine(_workspaceRoot, filePath);
                if (File.Exists(combined))
                {
                    filePath = combined;
                }
            }

            if (File.Exists(filePath))
            {
                string content = File.ReadAllText(filePath);
                string title = Path.GetFileName(filePath);
                SetDocument(title, content, filePath);
            }
        }
        catch (Exception ex)
        {
            SetDocument("Error Loading File", $"Failed to load '{filePath}': {ex.Message}", filePath);
        }
    }

    public void SetDocument(string title, string content, string filePath = "")
    {
        var preview = new DocumentPreviewModel
        {
            Title = title,
            FilePath = filePath,
            RawContent = content,
            LastModified = DateTime.Now
        };

        // 1. Calculate word count and token estimation
        preview.WordCount = CountWords(content);
        preview.EstimatedTokens = (int)Math.Ceiling(preview.WordCount * 1.33);

        // 2. Parse semantic AST using Glacier.DocTree
        try
        {
            preview.AstRoot = SpanMarkdownParser.Parse(content.AsSpan());
            preview.Outline = ExtractOutline(preview.AstRoot, content);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GlacierDocTreePreviewService] AST Parse error: {ex.Message}");
            preview.Outline = FallbackExtractOutline(content);
        }

        // 3. Render formatted HTML with heading anchors for click-to-scroll navigation
        preview.FormattedHtml = RenderFormattedDocument(content, preview.Outline);

        _activeDocument = preview;
        OnDocumentChanged?.Invoke();
    }

    public List<string> GetWorkspaceAvailableFiles()
    {
        var result = new List<string>();
        try
        {
            if (Directory.Exists(_workspaceRoot))
            {
                // Top level markdown and config docs
                foreach (var file in Directory.GetFiles(_workspaceRoot, "*.md"))
                {
                    result.Add(Path.GetFileName(file));
                }
            }
        }
        catch { }
        return result;
    }

    private class HeadingOccurrence
    {
        public int Level { get; set; }
        public string Title { get; set; } = "";
        public int LineNumber { get; set; }
    }

    private static List<DocOutlineItem> ExtractOutline(DocNode root, string content)
    {
        var list = new List<DocOutlineItem>();
        var headingOccurrences = ParseHeadingLines(content);
        int matchIndex = 0;

        void Traverse(DocNode node)
        {
            if (node.Type >= NodeType.Header1 && node.Type <= NodeType.Header6)
            {
                int level = (int)node.Type;
                string title = node.Content.Trim();

                int lineNumber = 1;
                for (int i = matchIndex; i < headingOccurrences.Count; i++)
                {
                    var occ = headingOccurrences[i];
                    if (occ.Level == level && string.Equals(occ.Title, title, StringComparison.OrdinalIgnoreCase))
                    {
                        lineNumber = occ.LineNumber;
                        matchIndex = i + 1;
                        break;
                    }
                }

                if (lineNumber == 1 && headingOccurrences.Count > 0)
                {
                    var anyOcc = headingOccurrences.Find(o => o.Level == level && string.Equals(o.Title, title, StringComparison.OrdinalIgnoreCase));
                    if (anyOcc != null) lineNumber = anyOcc.LineNumber;
                }

                list.Add(new DocOutlineItem
                {
                    Level = level,
                    Title = title,
                    LineNumber = lineNumber
                });
            }

            foreach (var child in node.Children)
            {
                Traverse(child);
            }
        }

        Traverse(root);
        return list;
    }

    private static List<HeadingOccurrence> ParseHeadingLines(string content)
    {
        var list = new List<HeadingOccurrence>();
        using var reader = new StringReader(content);
        string? line;
        int lineNum = 0;
        bool inCode = false;

        while ((line = reader.ReadLine()) != null)
        {
            lineNum++;
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```"))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode) continue;

            if (trimmed.StartsWith("#"))
            {
                int level = 0;
                while (level < trimmed.Length && trimmed[level] == '#') level++;
                if (level >= 1 && level <= 6 && (level == trimmed.Length || char.IsWhiteSpace(trimmed[level])))
                {
                    list.Add(new HeadingOccurrence
                    {
                        Level = level,
                        Title = trimmed[level..].Trim(),
                        LineNumber = lineNum
                    });
                }
            }
        }
        return list;
    }

    private static string RenderFormattedDocument(string content, List<DocOutlineItem> outline)
    {
        string rawHtml = GlacierMarkdownRenderer.Render(content);
        if (outline.Count == 0) return rawHtml;

        // Add line anchor attributes to headings for scroll navigation
        foreach (var item in outline)
        {
            string oldTag = $"<h{item.Level} class=\"gs-md-h{item.Level}\">";
            string newTag = $"<h{item.Level} id=\"heading-line-{item.LineNumber}\" class=\"gs-md-h{item.Level} gs-nav-heading\" data-line=\"{item.LineNumber}\">";

            // Replace the first matching instance
            int idx = rawHtml.IndexOf(oldTag, StringComparison.Ordinal);
            if (idx >= 0)
            {
                rawHtml = rawHtml.Remove(idx, oldTag.Length).Insert(idx, newTag);
            }
        }

        return rawHtml;
    }

    private static List<DocOutlineItem> FallbackExtractOutline(string content)
    {
        var list = new List<DocOutlineItem>();
        using var reader = new StringReader(content);
        string? line;
        int lineNum = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNum++;
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("#"))
            {
                int level = 0;
                while (level < trimmed.Length && trimmed[level] == '#') level++;
                if (level >= 1 && level <= 6 && (level == trimmed.Length || trimmed[level] == ' '))
                {
                    list.Add(new DocOutlineItem
                    {
                        Level = level,
                        Title = trimmed[level..].Trim(),
                        LineNumber = lineNum
                    });
                }
            }
        }
        return list;
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var matches = Regex.Matches(text, @"[\w\d]+");
        return matches.Count;
    }
}
