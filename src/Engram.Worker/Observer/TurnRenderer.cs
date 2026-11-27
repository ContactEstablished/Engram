using System.Text;
using System.Text.RegularExpressions;
using Engram.Core.Security;
using Engram.Worker.Capture;

namespace Engram.Worker.Observer;

/// <summary>
/// Turns a stored turn into the text the summarizer sees. Redacts again (a rule added since capture still
/// applies), neutralizes our own delimiter tags inside the data, and shrinks tool output until the whole
/// thing fits the budget.
/// </summary>
internal static partial class TurnRenderer
{
    private static readonly int[] FieldCaps = [4096, 1500, 600, 250];

    public static string RenderTurn(TurnInput turn, int maxChars, Redactor redactor)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(redactor);
        if (string.IsNullOrWhiteSpace(turn.Prompt) && turn.Events.Count == 0 && string.IsNullOrWhiteSpace(turn.FinalMessage))
        {
            throw new NothingToObserveException("empty turn");
        }

        string text = string.Empty;
        foreach (var cap in FieldCaps)
        {
            text = Build(turn, cap, redactor);
            if (text.Length <= maxChars)
            {
                return text;
            }
        }

        // Still too long (huge prompt or many calls): hard cut, keeping the start and the end.
        return PayloadShaper.Truncate(text, maxChars);
    }

    public static string RenderSession(SessionInput session, int maxChars, Redactor redactor)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(redactor);
        if (session.Prompts.Count == 0 && session.Observations.Count == 0)
        {
            throw new NothingToObserveException("empty session");
        }

        var sb = new StringBuilder();
        sb.Append("<session project=\"").Append(Attr(session.ProjectName)).Append('"');
        if (session.Branch is not null)
        {
            sb.Append(" branch=\"").Append(Attr(session.Branch)).Append('"');
        }

        sb.AppendLine(">");
        sb.AppendLine("<user_requests>");
        for (var i = 0; i < session.Prompts.Count; i++)
        {
            sb.Append(i + 1).Append(". ").AppendLine(Data(PayloadShaper.Truncate(session.Prompts[i], 600), redactor));
        }

        sb.AppendLine("</user_requests>");
        sb.AppendLine("<observations>");
        foreach (var o in session.Observations)
        {
            sb.Append('[').Append(o.Type).Append("] ").Append(Data(o.Title, redactor)).Append(": ").AppendLine(Data(o.Narrative, redactor));
        }

        sb.AppendLine("</observations>");
        sb.Append("</session>");
        var text = sb.ToString().ReplaceLineEndings("\n");
        return text.Length <= maxChars ? text : PayloadShaper.Truncate(text, maxChars);
    }

    /// <summary>A saved note: its metadata as attributes, the text as data. Over budget, the start and end are kept.</summary>
    public static string RenderNote(NoteInput note, int maxChars, Redactor redactor)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(redactor);
        if (string.IsNullOrWhiteSpace(note.Text))
        {
            throw new NothingToObserveException("empty note");
        }

        var sb = new StringBuilder();
        sb.Append("<note project=\"").Append(Attr(note.ProjectName)).Append('"');
        if (note.Branch is not null)
        {
            sb.Append(" branch=\"").Append(Attr(note.Branch)).Append('"');
        }

        if (note.Source is not null)
        {
            sb.Append(" source=\"").Append(Attr(redactor.Redact(note.Source))).Append('"');
        }

        if (note.TypeGiven)
        {
            sb.Append(" type=\"").Append(Attr(note.Type)).Append('"');
        }

        if (note.TitleGiven)
        {
            sb.Append(" title=\"").Append(Attr(redactor.Redact(note.Title))).Append('"');
        }

        sb.AppendLine(">");
        var budget = Math.Max(1000, maxChars - sb.Length - 16);
        sb.AppendLine(Data(PayloadShaper.Truncate(note.Text, budget), redactor));
        sb.Append("</note>");
        return sb.ToString().ReplaceLineEndings("\n");
    }

    private static string Build(TurnInput turn, int cap, Redactor redactor)
    {
        var sb = new StringBuilder();
        sb.Append("<turn project=\"").Append(Attr(turn.ProjectName)).Append('"');
        if (turn.Branch is not null)
        {
            sb.Append(" branch=\"").Append(Attr(turn.Branch)).Append('"');
        }

        sb.AppendLine(">");
        if (!string.IsNullOrWhiteSpace(turn.Prompt))
        {
            sb.AppendLine("<user_prompt>").AppendLine(Data(PayloadShaper.Truncate(turn.Prompt, cap * 4), redactor)).AppendLine("</user_prompt>");
        }

        if (turn.Events.Count > 0)
        {
            sb.AppendLine("<tool_calls>");
            for (var i = 0; i < turn.Events.Count; i++)
            {
                var e = turn.Events[i];
                sb.Append('[').Append(i + 1).Append("] ").Append(e.ToolName ?? e.HookEvent);
                if (e.AgentId is not null)
                {
                    sb.Append(" (inside a subagent)");
                }

                if (e.IsError)
                {
                    sb.Append(" FAILED");
                }

                sb.AppendLine();
                AppendField(sb, "input", e.ToolInput, cap, redactor);
                AppendField(sb, "output", e.ToolResponse, cap, redactor);
                AppendField(sb, "error", e.IsError ? e.Text : null, cap, redactor);
            }

            sb.AppendLine("</tool_calls>");
        }

        if (!string.IsNullOrWhiteSpace(turn.FinalMessage))
        {
            sb.AppendLine("<assistant_final_answer>").AppendLine(Data(PayloadShaper.Truncate(turn.FinalMessage, cap * 4), redactor)).AppendLine("</assistant_final_answer>");
        }

        sb.Append("</turn>");
        return sb.ToString().ReplaceLineEndings("\n");
    }

    private static void AppendField(StringBuilder sb, string label, string? value, int cap, Redactor redactor)
    {
        if (!string.IsNullOrEmpty(value))
        {
            sb.Append(label).Append(": ").AppendLine(Data(PayloadShaper.Truncate(value, cap), redactor));
        }
    }

    /// <summary>Redacts, and defuses any of our delimiter tags inside the data so it can't close a section early.</summary>
    private static string Data(string value, Redactor redactor) => DelimiterRegex().Replace(redactor.Redact(value), "<​/$1");

    private static string Attr(string value) => value.Replace("\"", "'", StringComparison.Ordinal).Replace("<", string.Empty, StringComparison.Ordinal);

    [GeneratedRegex(@"</(turn|user_prompt|tool_calls|assistant_final_answer|session|user_requests|observations|note)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DelimiterRegex();
}
