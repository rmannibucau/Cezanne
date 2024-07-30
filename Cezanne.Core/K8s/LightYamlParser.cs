using System.Collections.Immutable;

namespace Cezanne.Core.K8s
{
    // todo: use YamlDotNet but keep it as being optional (todo in the rest of the code too)
    public class LightYamlParser
    {
        public object Parse(TextReader reader)
        {
            return _Parse(new YamlReader(reader), 0, new LazyList(), _ToResult);
        }

        private object _Parse(
            YamlReader reader,
            int prefixLength,
            LazyList list,
            Func<LazyList, IDictionary<string, object>?, object> extractor
        )
        {
            IDictionary<string, object>? objectModel = null;

            string? line;
            var lineNumber = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                var firstChar = _FindNextChar(0, line);
                if (firstChar >= line.Length)
                {
                    continue;
                }

                var c = line[firstChar];
                switch (c)
                {
                    case '#':
                        continue;
                    case '-':
                    {
                        if (firstChar != prefixLength - 2)
                        {
                            reader.Line = line; // re-read it in the enclosing context
                            return extractor(list, objectModel);
                        }

                        var firstCollectionChar = _FindNextChar(firstChar + 1, line);
                        if (firstCollectionChar >= line.Length)
                        {
                            throw new InvalidOperationException(
                                $"Invalid collection on line {lineNumber}"
                            );
                        }

                        if (list.List is null)
                        {
                            list.List ??= new List<object>();
                        }

                        var sep = line.IndexOf(':', firstCollectionChar);
                        if (sep > 0)
                        {
                            // reparse the line as an object
                            reader.Line = line[..firstChar] + ' ' + line[(firstChar + 1)..];
                            var listObject = _Parse(
                                reader,
                                prefixLength,
                                list,
                                (_, obj) => obj ?? throw new ArgumentNullException(nameof(obj))
                            );
                            list.List.Add(listObject);
                        }
                        else
                        {
                            // likely a scalar - parsed as string for now (or empty dict/list for nested objects)
                            list.List.Add(_ToValue(line, firstCollectionChar));
                        }

                        break;
                    }
                    default:
                    {
                        // todo: check it is a valid char?
                        if (firstChar != prefixLength)
                        {
                            reader.Line = line; // let caller re-read the line, was not belonging to this parsing
                            return extractor(list, objectModel);
                        }

                        var sep = line.IndexOf(':');
                        if (sep < 0)
                        {
                            throw new ArgumentException($"No separator on line {lineNumber}");
                        }

                        objectModel ??= new Dictionary<string, object>();

                        var key = line.Substring(firstChar, sep - firstChar);
                        var dataStart = _FindNextChar(sep + 1, line);
                        if (dataStart == line.Length)
                        {
                            // object start
                            var nested = _Parse(
                                reader,
                                prefixLength + 2,
                                new LazyList(),
                                _ToResult
                            );
                            objectModel[key] = nested;
                        }
                        else
                        {
                            objectModel[key] = _ToValue(line, dataStart);
                        }

                        break;
                    }
                }
            }

            return extractor(list, objectModel);
        }

        private object _ToValue(string line, int dataStart)
        {
            var value = line[dataStart..].Trim();
            if ("{}".Equals(value) || "{ }".Equals(value))
            {
                return ImmutableDictionary<string, object>.Empty;
            }

            if ("[]".Equals(value) || "[ ]".Equals(value))
            {
                return ImmutableList<object>.Empty;
            }

            if (
                (value.StartsWith("\"") && value.EndsWith("\""))
                || (value.StartsWith("'") && value.EndsWith("'"))
            )
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value;
        }

        private object _ToResult(LazyList list, IDictionary<string, object>? objectModel)
        {
            if (list.List is not null)
            {
                return list.List.Reverse();
            }

            return objectModel ?? new Dictionary<string, object>();
        }

        private int _FindNextChar(int fromValue, string line)
        {
            var from = fromValue;
            while (line.Length > from && char.IsWhiteSpace(line[from]))
            {
                from++;
            }

            return from;
        }

        private class YamlReader
        {
            private readonly TextReader _delegate;

            public YamlReader(TextReader reader)
            {
                _delegate = reader;
            }

            public string? Line { get; set; }

            public string? ReadLine()
            {
                if (Line != null)
                {
                    var l = Line;
                    Line = null;
                    return l;
                }

                return _delegate.ReadLine();
            }
        }

        private class LazyList
        {
            public IList<object>? List { get; set; }
        }
    }
}
