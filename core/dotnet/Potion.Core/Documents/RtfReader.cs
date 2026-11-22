using System.Globalization;
using System.Text;

namespace Potion.Core.Documents;

/// <summary>
/// Reads RTF: paragraphs with alignment, bold/italic/underline/strike/super/subscript, fonts, sizes and colors,
/// Unicode and code-page text, line and page breaks, lists (\ls with \listtext), tables (\trowd…\cell…\row),
/// hyperlink fields, PNG/JPEG pictures and the page setup. Unknown destinations are skipped, as RTF requires.
/// </summary>
public static class RtfReader
{
    static RtfReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Document Read(string rtf) => new Parser(rtf).Parse();

    private sealed class State
    {
        public TextStyle Style = TextStyle.Plain;
        public Alignment Alignment;
        public bool InTable;
        public int List;          // \ls index, 0 = none
        public int ListLevel;
        public int Outline = -1;  // \outlinelevelN: heading N+1
        public int Skip;          // \ucN: characters to skip after \u
        public string Destination = "";   // "", "fonttbl", "colortbl", "pict", "fldinst", "fldrslt", "listtext", "skip"…
        public State Clone() => (State)MemberwiseClone();
    }

    private sealed class Parser(string rtf)
    {
        private int _pos;
        private readonly Stack<State> _stack = new();
        private State _state = new();
        private readonly Document _document = new();
        private List<Inline> _inlines = [];
        private readonly List<string> _fonts = [];
        private readonly Dictionary<int, string> _fontNames = [];
        private readonly List<string?> _colors = [];
        private readonly StringBuilder _fontName = new();
        private bool _colorSet;
        private int _fontIndex;
        private Encoding _codePage = Encoding.GetEncoding(1252);
        private readonly List<byte> _pendingBytes = [];
        // Tables
        private List<List<TableCell>>? _rows;
        private List<TableCell>? _row;
        private List<Block>? _cell;
        // Lists
        private readonly StringBuilder _listText = new();
        private readonly Dictionary<int, int> _listCounters = [];
        // Fields and pictures
        private readonly StringBuilder _fieldInstruction = new();
        private string? _fieldLink;
        private readonly StringBuilder _pictHex = new();
        private string _pictType = "";
        private double _pictW, _pictH, _pictGoalW, _pictGoalH;
        // Page
        private double? _paperW, _paperH, _margL, _margR, _margT, _margB;

        public Document Parse()
        {
            if (!rtf.TrimStart().StartsWith(@"{\rtf", StringComparison.Ordinal)) throw new InvalidDataException("not RTF");
            while (_pos < rtf.Length)
            {
                var c = rtf[_pos];
                switch (c)
                {
                    case '{': FlushBytes(); _stack.Push(_state); _state = _state.Clone(); _pos++; break;
                    case '}': FlushBytes(); EndGroup(); _pos++; break;
                    case '\\': ControlWord(); break;
                    case '\r' or '\n': _pos++; break;
                    default: FlushBytes(); Character(c.ToString()); _pos++; break;
                }
            }
            FlushBytes();
            EndParagraph(force: false);
            EndTable();
            if (_paperW is { } w && _paperH is { } h)
                _document.Page = new PageSetup(w, h, _margT ?? 72, _margR ?? 72, _margB ?? 72, _margL ?? 72);
            return _document;
        }

        private void EndGroup()
        {
            var closing = _state;
            _state = _stack.Count > 0 ? _stack.Pop() : new State();
            switch (closing.Destination)
            {
                case "pict" when _state.Destination != "pict": AddPicture(); break;
                case "fldinst" when _state.Destination != "fldinst":
                    var m = System.Text.RegularExpressions.Regex.Match(_fieldInstruction.ToString(), @"HYPERLINK\s+""([^""]+)""");
                    _fieldLink = m.Success && HtmlReader.IsLink(m.Groups[1].Value) ? m.Groups[1].Value : null;
                    _fieldInstruction.Clear();
                    break;
                case "fldrslt" when _state.Destination != "fldrslt": _fieldLink = null; break;
            }
            if (closing.Destination == "fonttbl") SaveFontName();
        }

        private void ControlWord()
        {
            _pos++;   // backslash
            if (_pos >= rtf.Length) return;
            var c = rtf[_pos];
            if (!char.IsLetter(c))
            {
                _pos++;
                switch (c)
                {
                    case '\'':
                        if (_pos + 2 <= rtf.Length && byte.TryParse(rtf.AsSpan(_pos, 2), NumberStyles.HexNumber, null, out var b))
                        {
                            if (_state.Skip > 0) _state.Skip--; else _pendingBytes.Add(b);
                        }
                        _pos += 2;
                        break;
                    case '*': _state.Destination = KeepsText(_state.Destination) ? "skip?" : _state.Destination; break;
                    case '~': Character(" "); break;
                    case '-': break;
                    case '_': Character("‑"); break;
                    case '\r' or '\n': FlushBytes(); EndParagraph(force: true); break;   // "\<newline>" is \par
                    default: FlushBytes(); Character(c.ToString()); break;
                }
                return;
            }
            var start = _pos;
            while (_pos < rtf.Length && char.IsLetter(rtf[_pos])) _pos++;
            var word = rtf[start.._pos];
            int? param = null;
            var numStart = _pos;
            if (_pos < rtf.Length && (rtf[_pos] == '-' || char.IsDigit(rtf[_pos])))
            {
                _pos++;
                while (_pos < rtf.Length && char.IsDigit(rtf[_pos])) _pos++;
                param = int.TryParse(rtf.AsSpan(numStart, _pos - numStart), out var p) ? p : null;
            }
            if (_pos < rtf.Length && rtf[_pos] == ' ') _pos++;   // the delimiter space belongs to the control word
            if (word != "u") FlushBytes();
            Apply(word, param);
        }

        private static bool KeepsText(string destination) => destination is "" or "fldrslt";

        private void Apply(string word, int? p)
        {
            // An ignorable destination (\*\something) we don't know: skip the whole group.
            if (_state.Destination == "skip?")
            {
                _state.Destination = word switch { "fldinst" => "fldinst", _ => "skip" };
                return;
            }
            if (_state.Destination == "skip") return;   // nothing inside a skipped group affects the document
            var on = p is null or not 0;
            var s = _state.Style;
            switch (word)
            {
                // Destinations
                case "fonttbl": _state.Destination = "fonttbl"; break;
                case "colortbl": _state.Destination = "colortbl"; _colors.Clear(); _colorSet = false; break;
                case "stylesheet" or "info" or "header" or "footer" or "headerl" or "headerr" or "footerl" or "footerr" or "footnote"
                    or "listtable" or "listoverridetable" or "rsidtbl" or "generator" or "xmlnstbl" or "themedata" or "colorschememapping"
                    or "latentstyles" or "datastore" or "pn" or "object" or "nonshppict" or "revtbl":
                    _state.Destination = "skip"; break;
                case "pict": _state.Destination = "pict"; _pictHex.Clear(); _pictType = ""; _pictW = _pictH = _pictGoalW = _pictGoalH = 0; break;
                case "fldinst": _state.Destination = "fldinst"; break;
                case "fldrslt": _state.Destination = "fldrslt"; break;
                case "listtext" or "pntext": _state.Destination = "listtext"; _listText.Clear(); break;
                // Picture properties
                case "pngblip": _pictType = "image/png"; break;
                case "jpegblip": _pictType = "image/jpeg"; break;
                case "picw": _pictW = p ?? 0; break;
                case "pich": _pictH = p ?? 0; break;
                case "picwgoal": _pictGoalW = (p ?? 0) / 20.0; break;
                case "pichgoal": _pictGoalH = (p ?? 0) / 20.0; break;
                // Character formatting
                case "b": _state.Style = s with { Bold = on }; break;
                case "i": _state.Style = s with { Italic = on }; break;
                case "ul": _state.Style = s with { Underline = on }; break;
                case "ulnone": _state.Style = s with { Underline = false }; break;
                case "strike" or "striked": _state.Style = s with { Strike = on }; break;
                case "super": _state.Style = s with { Superscript = true, Subscript = false }; break;
                case "sub": _state.Style = s with { Subscript = true, Superscript = false }; break;
                case "nosupersub": _state.Style = s with { Superscript = false, Subscript = false }; break;
                case "plain": _state.Style = TextStyle.Plain with { Link = s.Link }; break;
                case "fs": _state.Style = s with { FontSize = (p ?? 24) / 2.0 }; break;
                case "f":
                    if (_state.Destination == "fonttbl") { SaveFontName(); _fontIndex = p ?? 0; }
                    else _state.Style = s with { FontFamily = _fontNames.GetValueOrDefault(p ?? 0) };
                    break;
                case "cf": _state.Style = s with { Color = (p ?? 0) < _colors.Count ? _colors[p ?? 0] : null }; break;
                case "red" or "green" or "blue" when _state.Destination == "colortbl": ColorComponent(word, p ?? 0); break;
                case "ansicpg": try { _codePage = Encoding.GetEncoding(p ?? 1252); } catch (ArgumentException) { } break;
                case "uc": _state.Skip = 0; break;
                case "u":
                    var code = p ?? 0;
                    if (code < 0) code += 65536;
                    Character(((char)code).ToString());
                    _state.Skip = 1;
                    break;
                // Paragraphs
                case "pard": _state.Alignment = Alignment.Start; _state.InTable = false; _state.List = 0; _state.ListLevel = 0; _state.Outline = -1; break;
                case "outlinelevel": _state.Outline = p is >= 0 and < 6 ? p.Value : -1; break;
                case "ql": _state.Alignment = Alignment.Start; break;
                case "qc": _state.Alignment = Alignment.Center; break;
                case "qr": _state.Alignment = Alignment.End; break;
                case "qj": _state.Alignment = Alignment.Justify; break;
                case "par" when KeepsText(_state.Destination): EndParagraph(force: true); break;
                case "line": if (KeepsText(_state.Destination)) _inlines.Add(new LineBreak()); break;
                case "tab": Character("\t"); break;
                case "page" when KeepsText(_state.Destination): EndParagraph(force: false); if (_rows is null) _document.Blocks.Add(new PageBreak()); break;
                case "emdash": Character("—"); break;
                case "endash": Character("–"); break;
                case "bullet": Character("•"); break;
                case "lquote": Character("‘"); break;
                case "rquote": Character("’"); break;
                case "ldblquote": Character("“"); break;
                case "rdblquote": Character("”"); break;
                case "ls": _state.List = p ?? 0; break;
                case "ilvl": _state.ListLevel = p ?? 0; break;
                // Tables
                case "intbl": _state.InTable = true; break;
                case "trowd": _rows ??= []; _row ??= []; break;
                case "cell": EndParagraph(force: false); _row ??= []; _row.Add(new TableCell(_cell ?? [])); _cell = null; break;
                case "row": if (_row is { Count: > 0 }) { (_rows ??= []).Add(_row); } _row = null; break;
                // Page
                case "paperw": _paperW = (p ?? 12240) / 20.0; break;
                case "paperh": _paperH = (p ?? 15840) / 20.0; break;
                case "margl": _margL = (p ?? 1440) / 20.0; break;
                case "margr": _margR = (p ?? 1440) / 20.0; break;
                case "margt": _margT = (p ?? 1440) / 20.0; break;
                case "margb": _margB = (p ?? 1440) / 20.0; break;
            }
        }

        private int _r, _g, _b;
        private void ColorComponent(string word, int value)
        {
            if (word == "red") _r = value; else if (word == "green") _g = value; else _b = value;
            _colorSet = true;
        }

        /// <summary>Font names end with ";" (grouped or ungrouped font tables).</summary>
        private void SaveFontName()
        {
            var name = _fontName.ToString().Trim().TrimEnd(';').Trim();
            if (name.Length > 0) _fontNames[_fontIndex] = name;
            _fontName.Clear();
        }

        private void Character(string text)
        {
            if (_state.Skip > 0 && text.Length == 1 && _state.Destination != "colortbl") { _state.Skip--; return; }
            switch (_state.Destination)
            {
                case "" or "fldrslt":
                    if (_rows is not null && !_state.InTable && _row is null && _cell is null) EndTable();
                    var style = _fieldLink is not null ? _state.Style with { Link = _fieldLink, Underline = false, Color = null } : _state.Style;
                    if (_inlines.Count > 0 && _inlines[^1] is TextRun last && last.Style == style) _inlines[^1] = last with { Text = last.Text + text };
                    else _inlines.Add(new TextRun(text, style));
                    break;
                case "fonttbl":
                    _fontName.Append(text);
                    if (text.Contains(';')) SaveFontName();
                    break;
                case "colortbl":
                    // Each ";" ends one color; an entry without components is the automatic color.
                    foreach (var _ in text.Where(ch => ch == ';'))
                    {
                        _colors.Add(_colorSet ? $"{_r:X2}{_g:X2}{_b:X2}" : null);
                        _r = _g = _b = 0;
                        _colorSet = false;
                    }
                    break;
                case "pict": _pictHex.Append(text); break;
                case "fldinst": _fieldInstruction.Append(text); break;
                case "listtext": _listText.Append(text); break;
            }
        }

        private void FlushBytes()
        {
            if (_pendingBytes.Count == 0) return;
            var text = _codePage.GetString([.. _pendingBytes]);
            _pendingBytes.Clear();
            Character(text);
        }

        private void AddPicture()
        {
            if (_pictType.Length == 0 || _pictHex.Length < 2) return;
            var hex = new string(_pictHex.ToString().Where(Uri.IsHexDigit).ToArray());
            if (hex.Length % 2 == 1) hex = hex[..^1];
            byte[] bytes;
            try { bytes = Convert.FromHexString(hex); } catch (FormatException) { return; }
            var (pxW, pxH) = ImageSize.Of(bytes);
            double width = _pictGoalW > 0 ? _pictGoalW : pxW * 0.75, height = _pictGoalH > 0 ? _pictGoalH : pxH * 0.75;
            if (width <= 0 || height <= 0) return;
            _inlines.Add(new ImageRun(bytes, _pictType, width, height));
        }

        private void EndParagraph(bool force)
        {
            var inlines = _inlines;
            _inlines = [];
            var hasContent = inlines.Any(i => i is not TextRun t || t.Text.Trim().Length > 0);
            Block block;
            if (_state.List > 0 && hasContent)
            {
                var marker = _listText.ToString().Trim();
                var ordered = marker.Length > 0 && char.IsDigit(marker[0]);
                var number = ordered && int.TryParse(new string(marker.TakeWhile(char.IsDigit).ToArray()), out var n) ? n
                    : _listCounters.TryGetValue(_state.List, out var c) ? c + 1 : 1;
                _listCounters[_state.List] = number;
                block = new ListItem(TrimStart(inlines), _state.ListLevel, ordered, number, _state.List);
            }
            else if (hasContent || force)
            {
                block = new Paragraph(hasContent ? inlines : [], _state.Outline >= 0 && hasContent ? _state.Outline + 1 : 0, _state.Alignment);
            }
            else return;
            _listText.Clear();

            if (_state.InTable || _cell is not null) (_cell ??= []).Add(block);
            else _document.Blocks.Add(block);
        }

        private static List<Inline> TrimStart(List<Inline> inlines)
        {
            if (inlines.Count > 0 && inlines[0] is TextRun first) inlines[0] = first with { Text = first.Text.TrimStart() };
            return inlines;
        }

        private void EndTable()
        {
            if (_rows is { Count: > 0 } rows) _document.Blocks.Add(new Table(rows, HasHeader: false));
            _rows = null;
        }
    }
}
