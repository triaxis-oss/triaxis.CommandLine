namespace triaxis.CommandLine;

using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

/// <summary>
/// The YAML counterpart of <see cref="JsonConfigurationEditor"/>. Only scalar nodes
/// carry source text; every byte that is not a scalar — indentation, <c>-</c>/<c>:</c>
/// punctuation, blank lines, comments — stays in the gap that precedes the next
/// scalar and round-trips verbatim. Edits touch only the scalars a change names; a
/// block sequence that has to gain a key is rewritten in place to a block mapping.
/// Constructs that can't be edited safely (flow style, anchors) fall back to a
/// freshly serialised document.
/// </summary>
internal static class YamlConfigurationEditor
{
    private sealed class UneditableException : Exception;

    private struct Tok
    {
        public string Lead;
        public string Text;
    }

    private abstract class Node;

    private sealed class Scalar : Node
    {
        public Tok Tok;
        public string Decoded = "";
    }

    private sealed class Member
    {
        public Scalar Key = null!;
        public string Name = "";
        public Node Value = null!;
    }

    private sealed class Map : Node
    {
        public List<Member> Members = [];
    }

    private sealed class Elem
    {
        public Node Value = null!;
    }

    private sealed class Seq : Node
    {
        public List<Elem> Elems = [];
    }

    public static string Apply(string original, IEnumerable<KeyValuePair<string, string?>> changes)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return Fresh(changes);
        }

        try
        {
            var parser = new Parser(new StringReader(original));
            int last = 0;
            Node root = ParseDocument(parser, original, ref last);
            string trailing = original.Substring(Math.Min(last, original.Length));

            if (root is not Map map)
            {
                return Fresh(changes);
            }

            foreach (var change in changes)
            {
                ApplyOne(map, change.Key.Split(':'), 0, change.Value);
            }

            var sb = new StringBuilder();
            Render(map, sb);
            sb.Append(trailing);
            return sb.ToString();
        }
        catch (UneditableException)
        {
            return Fresh(changes);
        }
        catch (YamlException)
        {
            return Fresh(changes);
        }
    }

    // ---- transform -----------------------------------------------------------

    private static void ApplyOne(Map map, string[] path, int depth, string? value)
    {
        string key = path[depth];
        bool last = depth == path.Length - 1;
        Member? member = map.Members.Find(m => m.Name == key);

        if (last && value is null)
        {
            if (member is not null)
            {
                map.Members.Remove(member);
            }
            return;
        }

        if (member is null)
        {
            member = NewMember(map, key);
            member.Value = last
                ? NewScalar(value!, ": ")
                : new Map();
            map.Members.Add(member);
        }

        if (last)
        {
            if (member.Value is Scalar s)
            {
                s.Tok.Text = YamlScalar(value!);
            }
            else
            {
                member.Value = NewScalar(value!, ": ");
            }
            return;
        }

        if (member.Value is Seq seq)
        {
            member.Value = ToMap(seq);
        }
        if (member.Value is not Map child)
        {
            member.Value = child = new Map();
        }
        ApplyOne(child, path, depth + 1, value);
    }

    private static Member NewMember(Map map, string key)
    {
        // Mirror an existing sibling's pre-key run (newline + indent); for an empty
        // mapping there is nothing on disk to anchor to, so the document must be
        // rebuilt rather than guessing an indentation that may be wrong.
        if (map.Members.Count == 0)
        {
            throw new UneditableException();
        }

        string lead = map.Members[map.Members.Count - 1].Key.Tok.Lead;
        return new Member
        {
            Name = key,
            Key = new Scalar { Tok = new Tok { Lead = lead, Text = YamlScalar(key) } },
        };
    }

    private static Map ToMap(Seq seq)
    {
        var map = new Map();
        for (int i = 0; i < seq.Elems.Count; i++)
        {
            Node v = seq.Elems[i].Value;
            // The element's scalar lead looks like "<newline><indent>- "; reuse the
            // indent for the new key and drop the "- " sequence marker.
            string elemLead = FirstLead(v);
            int dash = elemLead.LastIndexOf('-');
            if (dash < 0 || elemLead.Substring(dash + 1).TrimStart(' ').Length != 0)
            {
                // Not a plain block item ("- value") — don't risk a broken rewrite.
                throw new UneditableException();
            }

            SetFirstLead(v, ": ");
            map.Members.Add(new Member
            {
                Name = i.ToString(),
                Key = new Scalar { Tok = new Tok { Lead = elemLead.Substring(0, dash), Text = YamlScalar(i.ToString()) } },
                Value = v,
            });
        }
        return map;
    }

    private static Scalar NewScalar(string value, string lead)
        => new() { Tok = new Tok { Lead = lead, Text = YamlScalar(value) } };

    private static string FirstLead(Node n) => n switch
    {
        Scalar s => s.Tok.Lead,
        Map m when m.Members.Count > 0 => m.Members[0].Key.Tok.Lead,
        Seq q when q.Elems.Count > 0 => FirstLead(q.Elems[0].Value),
        _ => throw new UneditableException(),
    };

    private static void SetFirstLead(Node n, string lead)
    {
        switch (n)
        {
            case Scalar s: s.Tok.Lead = lead; break;
            case Map m when m.Members.Count > 0: m.Members[0].Key.Tok.Lead = lead; break;
            case Seq q when q.Elems.Count > 0: SetFirstLead(q.Elems[0].Value, lead); break;
            default: throw new UneditableException();
        }
    }

    // ---- parse ---------------------------------------------------------------

    private static Node ParseDocument(IParser parser, string src, ref int last)
    {
        // StreamStart, DocumentStart, then the first content event. Everything
        // before the first scalar (directives, '---', leading comments) is captured
        // as that scalar's lead, so nothing is lost.
        if (!parser.MoveNext() || !parser.MoveNext() || !parser.MoveNext())
        {
            throw new UneditableException();
        }
        return ParseNode(parser, src, ref last);
    }

    private static Node ParseNode(IParser parser, string src, ref int last)
    {
        switch (parser.Current)
        {
            case MappingStart ms when ms.Style == MappingStyle.Flow:
                throw new UneditableException();

            case MappingStart:
                parser.MoveNext();
                var map = new Map();
                while (parser.Current is MappingStart or SequenceStart or YamlDotNet.Core.Events.Scalar)
                {
                    var key = ParseScalar(parser, src, ref last);
                    var value = ParseNode(parser, src, ref last);
                    map.Members.Add(new Member { Key = key, Name = key.Decoded, Value = value });
                }
                Expect<MappingEnd>(parser);
                parser.MoveNext();
                return map;

            case SequenceStart ss when ss.Style == SequenceStyle.Flow:
                throw new UneditableException();

            case SequenceStart:
                parser.MoveNext();
                var seq = new Seq();
                while (parser.Current is MappingStart or SequenceStart or YamlDotNet.Core.Events.Scalar)
                {
                    seq.Elems.Add(new Elem { Value = ParseNode(parser, src, ref last) });
                }
                Expect<SequenceEnd>(parser);
                parser.MoveNext();
                return seq;

            case YamlDotNet.Core.Events.Scalar:
                return ParseScalar(parser, src, ref last);

            default:
                // Aliases / anchors / null current and anything exotic: not editable.
                throw new UneditableException();
        }
    }

    private static void Expect<T>(IParser parser) where T : ParsingEvent
    {
        if (parser.Current is not T)
        {
            throw new UneditableException();
        }
    }

    private static Scalar ParseScalar(IParser parser, string src, ref int last)
    {
        if (parser.Current is not YamlDotNet.Core.Events.Scalar ev)
        {
            throw new UneditableException();
        }

        int start = (int)ev.Start.Index;
        int end = (int)ev.End.Index;
        var node = new Scalar
        {
            Decoded = ev.Value,
            Tok = new Tok { Lead = Slice(src, last, start), Text = Slice(src, start, end) },
        };
        last = end;
        parser.MoveNext();
        return node;
    }

    private static string Slice(string s, int from, int to)
    {
        from = Math.Max(0, Math.Min(from, s.Length));
        to = Math.Max(from, Math.Min(to, s.Length));
        return s.Substring(from, to - from);
    }

    // ---- render --------------------------------------------------------------

    private static void Render(Node n, StringBuilder sb)
    {
        switch (n)
        {
            case Scalar s:
                sb.Append(s.Tok.Lead).Append(s.Tok.Text);
                break;
            case Map m:
                foreach (var member in m.Members)
                {
                    Render(member.Key, sb);
                    Render(member.Value, sb);
                }
                break;
            case Seq q:
                foreach (var e in q.Elems)
                {
                    Render(e.Value, sb);
                }
                break;
        }
    }

    // A minimal, safe scalar rendering: single-quote when a plain scalar would be
    // ambiguous (empty, surrounding space, indicator chars, or a value YAML would
    // read back as a bool/number/null).
    private static string YamlScalar(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        bool needsQuote =
            value != value.Trim()
            || "!&*-?|>%@`\"'#,[]{}:".IndexOf(value[0]) >= 0
            || value.Contains(": ")
            || value.Contains(" #")
            || value.IndexOf('\n') >= 0
            || LooksScalarReserved(value);

        return needsQuote ? "'" + value.Replace("'", "''") + "'" : value;
    }

    private static bool LooksScalarReserved(string v)
    {
        switch (v.ToLowerInvariant())
        {
            case "true" or "false" or "null" or "~" or "yes" or "no" or "on" or "off":
                return true;
        }
        return double.TryParse(v, out _);
    }

    private static string Fresh(IEnumerable<KeyValuePair<string, string?>> changes)
        => new SerializerBuilder().Build()
            .Serialize(PersistentConfigurationFile.BuildTree(changes));
}
