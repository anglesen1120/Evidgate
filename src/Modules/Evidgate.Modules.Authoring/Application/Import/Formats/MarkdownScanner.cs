using Evidgate.Modules.Authoring.Domain;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

/// <summary>
/// The single-pass line scanner behind <see cref="MarkdownFormat"/>: one <c>user_story</c> row per ATX heading and one
/// <c>acceptance_criterion</c> row per list item or Gherkin block under it. It keeps one open block at a time, so memory
/// is bounded by the largest block, never by the file.
/// </summary>
internal sealed class MarkdownScanner
{
    private enum Mode
    {
        Top,
        Item,
        Run,
        Fence,
    }

    private readonly List<ImportRow> _rows = [];
    private readonly List<ImportIssue> _issues = [];
    private readonly List<string> _storyBody = [];
    private readonly List<string> _block = [];
    private Mode _mode = Mode.Top;
    private string? _storyKey;
    private int _storyIndex = -1;
    private int _storyLine;
    private int _stories;
    private int _criteria;
    private bool _storyOpen;
    private int _blockLine;
    private bool _orphan;
    private int _itemIndent;
    private int _blanks;
    private char _fence;
    private int _fenceLength;
    private string _fenceOpening = string.Empty;
    private string _fenceInfo = string.Empty;
    private bool _docString;

    /// <summary>True once the file holds more rows than <see cref="ImportLimits.MaxDataRows"/>.</summary>
    internal bool TooLarge => _rows.Count > ImportLimits.MaxDataRows;

    /// <summary>Reads one source line.</summary>
    /// <param name="line">The line without its terminator.</param>
    /// <param name="number">Its 1-based source line number.</param>
    internal void Feed(string line, int number)
    {
        if (!ImportText.IsStorable(line))
        {
            Refuse(number, "/row", "invalid_text");

            return;
        }

        switch (_mode)
        {
            case Mode.Item:
                FeedItem(line, number);
                break;
            case Mode.Run:
                FeedRun(line, number);
                break;
            case Mode.Fence:
                FeedFence(line);
                break;
            default:
                FeedTop(line, number);
                break;
        }
    }

    /// <summary>Closes whatever block the end of the file left open and returns the parse.</summary>
    internal ImportParse Finish()
    {
        switch (_mode)
        {
            case Mode.Item:
                CloseItem();
                break;
            case Mode.Run:
                CloseRun();
                break;
            case Mode.Fence:
                FinishFence(closing: null);
                break;
            default:
                break;
        }

        EndStoryBody();
        if (_rows.Count == 0 && _issues.Count == 0)
        {
            Refuse(0, "/rows", "value_invalid");
        }

        return _issues.Count > 0 ? ImportParse.Invalid(_issues) : ImportParse.Ok(_rows);
    }

    private void FeedTop(string line, int number)
    {
        if (MarkdownLines.IsBlank(line))
        {
            if (_storyOpen)
            {
                _storyBody.Add(string.Empty);
            }
        }
        else if (MarkdownLines.TryHeading(line, out var title))
        {
            StartStory(title, number);
        }
        else if (MarkdownLines.IsThematicBreak(line))
        {
            // A rule separates sections and carries no requirement text.
        }
        else if (MarkdownLines.TryMarker(line, out _, out var contentIndent, out var text))
        {
            StartItem(contentIndent, text, number);
        }
        else if (MarkdownLines.TryFence(line, out var fence, out var length, out var info))
        {
            StartFence(line, fence, length, info, number);
        }
        else if (MarkdownLines.IsStep(line))
        {
            StartRun(line, number);
        }
        else if (_storyOpen)
        {
            _storyBody.Add(line);
        }
    }

    private void StartStory(string heading, int number)
    {
        EndStoryBody();
        _stories++;
        _storyKey = "s" + _stories.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _storyLine = number;
        _storyOpen = true;
        _storyBody.Clear();

        var title = ImportText.Normalise(heading, out _) ?? string.Empty;
        if (title.Length == 0)
        {
            Refuse(number, "/title", "value_invalid");
        }
        else if (!AuthoringText.Fits(title, RequirementRules.MaximumTitleCodePoints))
        {
            // A heading is the story's whole identity, so a long one is refused rather than cut.
            Refuse(number, "/title", "length");
            title = MarkdownLines.FirstCodePoints(title, RequirementRules.MaximumTitleCodePoints);
        }

        _storyIndex = _rows.Count;
        _rows.Add(NewRow(_storyKey, null, "user_story", title, null, number));
    }

    private void EndStoryBody()
    {
        if (!_storyOpen)
        {
            return;
        }

        _storyOpen = false;
        var body = Joined(_storyBody);
        if (body.Length == 0)
        {
            return;
        }

        if (!RequirementRules.TryNormalizeBody(body, out var normalized))
        {
            Refuse(_storyLine, "/description", "length");

            return;
        }

        _rows[_storyIndex] = _rows[_storyIndex] with { Description = normalized };
    }

    private void StartItem(int contentIndent, string text, int number)
    {
        BeginBlock(Mode.Item, number);
        _block.Add(text);
        _itemIndent = contentIndent;
        _blanks = 0;
        _fence = '\0';
        OpenInnerFence(text);
    }

    private void FeedItem(string line, int number)
    {
        if (_fence != '\0')
        {
            var inner = MarkdownLines.Dedent(line, _itemIndent);
            _block.Add(inner);
            if (MarkdownLines.ClosesFence(inner, _fence, _fenceLength))
            {
                _fence = '\0';
            }

            return;
        }

        if (MarkdownLines.IsBlank(line))
        {
            _blanks++;

            return;
        }

        var indent = MarkdownLines.Indent(line, out _);
        if (MarkdownLines.TryMarker(line, out _, out _, out _) || (indent < _itemIndent && EndsItem(line)))
        {
            CloseItem();
            FeedTop(line, number);

            return;
        }

        for (; _blanks > 0; _blanks--)
        {
            _block.Add(string.Empty);
        }

        var continuation = MarkdownLines.Dedent(line, _itemIndent);
        _block.Add(continuation);
        OpenInnerFence(continuation);
    }

    /// <summary>A line less indented than the item's text ends the item when it follows a blank line or starts another construct.</summary>
    private bool EndsItem(string line) =>
        _blanks > 0
        || MarkdownLines.TryHeading(line, out _)
        || MarkdownLines.IsThematicBreak(line)
        || MarkdownLines.TryFence(line, out _, out _, out _);

    private void OpenInnerFence(string text)
    {
        if (MarkdownLines.TryFence(text, out var fence, out var length, out _))
        {
            _fence = fence;
            _fenceLength = length;
        }
    }

    private void CloseItem()
    {
        _mode = Mode.Top;
        if (!_orphan)
        {
            EmitCriterion(_blockLine);
        }
    }

    private void StartRun(string line, int number)
    {
        BeginBlock(Mode.Run, number);
        _block.Add(line);
    }

    private void FeedRun(string line, int number)
    {
        // A docstring is step argument text: its blank lines and any line inside belong to the criterion verbatim.
        if (_docString)
        {
            _block.Add(line);
            _docString = !MarkdownLines.ClosesDocString(line);

            return;
        }

        if (MarkdownLines.IsStep(line) || MarkdownLines.IsTableRow(line))
        {
            _block.Add(line);

            return;
        }

        if (MarkdownLines.OpensDocString(line))
        {
            _block.Add(line);
            _docString = true;

            return;
        }

        CloseRun();
        FeedTop(line, number);
    }

    private void CloseRun()
    {
        _mode = Mode.Top;
        if (!_orphan)
        {
            EmitCriterion(_blockLine);
        }
    }

    private void StartFence(string line, char fence, int length, string info, int number)
    {
        _mode = Mode.Fence;
        _block.Clear();
        _blockLine = number;
        _fence = fence;
        _fenceLength = length;
        _fenceOpening = line;
        _fenceInfo = info;
    }

    private void FeedFence(string line)
    {
        if (MarkdownLines.ClosesFence(line, _fence, _fenceLength))
        {
            FinishFence(line);
        }
        else
        {
            _block.Add(line);
        }
    }

    /// <summary>
    /// A top-level fence is one criterion when it is Gherkin (an info string of <c>gherkin</c>, or a first line that opens a
    /// feature, scenario or step); any other fence is plain story text before the first criterion and ignored after it.
    /// </summary>
    private void FinishFence(string? closing)
    {
        _mode = Mode.Top;
        _fence = '\0';
        if (IsGherkinFence())
        {
            if (_storyKey is null)
            {
                Refuse(_blockLine, "/row", "orphan_row");

                return;
            }

            EndStoryBody();
            EmitCriterion(_blockLine);

            return;
        }

        if (_storyOpen)
        {
            _storyBody.Add(_fenceOpening);
            _storyBody.AddRange(_block);
            if (closing is not null)
            {
                _storyBody.Add(closing);
            }
        }
    }

    private bool IsGherkinFence()
    {
        if (_fenceInfo.StartsWith("gherkin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var first = _block.Find(entry => !MarkdownLines.IsBlank(entry));

        return first is not null && MarkdownLines.IsGherkinOpening(first);
    }

    private void BeginBlock(Mode mode, int number)
    {
        EndStoryBody();
        _mode = mode;
        _block.Clear();
        _blockLine = number;
        _docString = false;
        _orphan = _storyKey is null;
        if (_orphan)
        {
            Refuse(number, "/row", "orphan_row");
        }
    }

    /// <summary>Turns the open block into one acceptance criterion: its text is the body and its first line the title.</summary>
    private void EmitCriterion(int number)
    {
        var body = Joined(_block);
        var first = body.Split('\n', 2)[0].Trim();
        var title = MarkdownLines.FirstCodePoints(ImportText.Normalise(first, out _) ?? string.Empty, RequirementRules.MaximumTitleCodePoints);
        if (title.Length == 0)
        {
            Refuse(number, "/title", "value_invalid");

            return;
        }

        if (!RequirementRules.TryNormalizeBody(body, out var normalized))
        {
            Refuse(number, "/description", "length");

            return;
        }

        _criteria++;
        var key = "a" + _criteria.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _rows.Add(NewRow(key, _storyKey, "acceptance_criterion", title, normalized, number));
    }

    private ImportRow NewRow(string key, string? parent, string kind, string title, string? body, int sourceLine) => new(
        _rows.Count + 1,
        ImportRowKind.Requirement,
        key,
        parent,
        null,
        kind,
        title,
        body,
        null,
        null,
        null,
        null,
        null,
        null,
        sourceLine);

    /// <summary>The lines joined with <c>\n</c>, without the blank lines at either end.</summary>
    private static string Joined(List<string> lines)
    {
        var start = 0;
        var end = lines.Count;
        while (start < end && MarkdownLines.IsBlank(lines[start]))
        {
            start++;
        }

        while (end > start && MarkdownLines.IsBlank(lines[end - 1]))
        {
            end--;
        }

        return string.Join('\n', lines.Skip(start).Take(end - start));
    }

    private void Refuse(int number, string path, string reason) => ImportSchema.Add(_issues, number, path, reason);
}
