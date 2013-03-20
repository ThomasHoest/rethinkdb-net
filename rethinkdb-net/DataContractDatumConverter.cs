﻿using System;
using System.IO;
using System.Text;
using RethinkDb.Spec;
using System.Globalization;

namespace RethinkDb
{
    class DataContractDatumConverter<T> : IDatumConverter<T>
    {
        private System.Runtime.Serialization.Json.DataContractJsonSerializer dcs;

        public DataContractDatumConverter()
        {
            dcs = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(T));
        }

        public DataContractDatumConverter(System.Runtime.Serialization.Json.DataContractJsonSerializer dcs)
        {
            this.dcs = dcs;
        }

        public T ConvertDatum(Datum datum)
        {
            // FIXME: the DataContractJsonSerializer approach seems completely wrong with the new rethinkdb protocol;
            // will need to figure out now how to map Datum objects into native objects ourselves.  In the mean time,
            // actually convert the Datum to Json text so that we can use DataContractJsonSerializer.

            StringBuilder builder = new StringBuilder();
            ConvertDatumToJson(builder, datum);

            var data = Encoding.UTF8.GetBytes(builder.ToString());
            using (var stream = new MemoryStream(data))
            {
                return (T)dcs.ReadObject(stream);
            }
        }

        public Datum ConvertObject(T obj)
        {
            // FIXME: You thought ConvertDatum was bad?  Check out this!  obj -> JSON -> JSON parser into Datum

            string jsonText;
            using (var stream = new MemoryStream())
            {
                dcs.WriteObject(stream, obj);
                jsonText = Encoding.UTF8.GetString(stream.ToArray());
            }

            var retval = ConvertJsonToDatum(jsonText);

            // Special case: if we generated a "null" idfield, let's remove it from the datum.  This is a hack
            // to let inserts work correctly with autogenerated ids; theoretically setting EmitDefaultValue=false
            // on the DataMemberAttribute would accomplish this, but that doesn't seem to work in Mono.
            if (retval.type == Datum.DatumType.R_OBJECT)
            {
                for (int i = 0; i < retval.r_object.Count; i++)
                {
                    if (retval.r_object[i].key == "id")
                    {
                        retval.r_object.RemoveAt(i);
                        break;
                    }
                }
            }

            return retval;

        }

        private void ConvertDatumToJson(StringBuilder builder, Datum datum)
        {
            switch (datum.type)
            {
                case Datum.DatumType.R_ARRAY:
                    builder.Append('[');
                    for (int i = 0; i < datum.r_array.Count; i++)
                    {
                        ConvertDatumToJson(builder, datum.r_array[i]);
                        if (i != (datum.r_array.Count - 1))
                            builder.Append(',');
                    }
                    builder.Append(']');
                    break;
                case Datum.DatumType.R_BOOL:
                    if (datum.r_bool)
                        builder.Append("true");
                    else
                        builder.Append("false");
                    break;
                case Datum.DatumType.R_NULL:
                    builder.Append("null");
                    break;
                case Datum.DatumType.R_NUM:
                    builder.Append(datum.r_num);
                    break;
                case Datum.DatumType.R_OBJECT:
                    builder.Append('{');
                    for (int i = 0; i < datum.r_object.Count; i++)
                    {
                        var key = datum.r_object[i].key;
                        var value = datum.r_object[i].val;
                        ConvertStringToJson(builder, key);
                        builder.Append(':');
                        ConvertDatumToJson(builder, value);
                        if (i != (datum.r_object.Count - 1))
                            builder.Append(',');
                    }
                    builder.Append('}');
                    break;
                case Datum.DatumType.R_STR:
                    ConvertStringToJson(builder, datum.r_str);
                    break;
                default:
                    throw new NotSupportedException("Unsupported datum type");
            }
        }

        private void ConvertStringToJson(StringBuilder builder, string str)
        {
            builder.Append('"');
            foreach (var c in str)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
        }

        private Datum ConvertJsonToDatum(string jsonText)
        {
            return Parse(new StringReader(jsonText));
        }

        internal static Datum Parse(TextReader reader)
        {
            int depthCount = 0;
            Datum retval = Parse(reader, ref depthCount);

            while (true)
            {
                int c = reader.Read();
                switch (c)
                {
                    case -1:
                        return retval;
                    case (int)' ':
                    case (int)'\t':
                    case (int)'\n':
                    case (int)'\r':
                        continue;
                    default:
                        throw new InvalidOperationException("JSON Parse Error");
                }
            }
        }

        private static Datum Parse(TextReader reader, ref int depthCount)
        {
            depthCount += 1;
            if (depthCount > 100)
                throw new Exception("Object or array nesting too deep to parse");
            try
            {
                while (true)
                {
                    int c = reader.Read();
                    switch (c)
                    {
                        case -1:
                            throw new InvalidOperationException("JSON Parse Error");
                        case (int)' ':
                        case (int)'\t':
                        case (int)'\n':
                        case (int)'\r':
                            continue;
                        case (int)'n':
                            {
                                int n1 = reader.Read();
                                int n2 = reader.Read();
                                int n3 = reader.Read();
                                if (n1 == (int)'u' && n2 == (int)'l' && n3 == (int)'l')
                                    return new Datum() { type = Datum.DatumType.R_NULL };
                                else
                                    throw new InvalidOperationException("JSON Parse Error");
                            }
                        case (int)'t':
                            {
                                int n1 = reader.Read();
                                int n2 = reader.Read();
                                int n3 = reader.Read();
                                if (n1 == (int)'r' && n2 == (int)'u' && n3 == (int)'e')
                                    return new Datum() { type = Datum.DatumType.R_BOOL, r_bool = true };
                                else
                                    throw new InvalidOperationException("JSON Parse Error");
                            }
                        case (int)'f':
                            {
                                int n1 = reader.Read();
                                int n2 = reader.Read();
                                int n3 = reader.Read();
                                int n4 = reader.Read();
                                if (n1 == (int)'a' && n2 == (int)'l' && n3 == (int)'s' && n4 == (int)'e')
                                    return new Datum() { type = Datum.DatumType.R_BOOL, r_bool = false };
                                else
                                    throw new InvalidOperationException("JSON Parse Error");
                            }
                        case (int)'0':
                        case (int)'1':
                        case (int)'2':
                        case (int)'3':
                        case (int)'4':
                        case (int)'5':
                        case (int)'6':
                        case (int)'7':
                        case (int)'8':
                        case (int)'9':
                        case (int)'-':
                        case (int)'.':
                            return ParseNumber(reader, (char)c);
                        case (int)'"':
                            return ParseString(reader);
                        case (int)'[':
                            return ParseArray(reader, ref depthCount);
                        case (int)'{':
                            return ParseObject(reader, ref depthCount);
                        default:
                            throw new InvalidOperationException("JSON Parse Error");
                    }
                }
            }
            finally
            {
                depthCount -= 1;
            }
        }

        private static Datum ParseString(TextReader reader)
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                int c = reader.Read();
                switch (c)
                {
                    case -1:
                        throw new InvalidOperationException("JSON Parse Error");
                    case '"':
                        return new Datum() { type = Datum.DatumType.R_STR, r_str = sb.ToString() };
                    case '\\':
                        {
                            int c2 = reader.Read();
                            switch (c2)
                            {
                                case -1:
                                    throw new InvalidOperationException("JSON Parse Error");
                                case (int)'"':
                                    sb.Append('"');
                                    break;
                                case (int)'\\':
                                    sb.Append('\\');
                                    break;
                                case (int)'/':
                                    sb.Append('/');
                                    break;
                                case (int)'b':
                                    sb.Append('\b');
                                    break;
                                case (int)'f':
                                    sb.Append('\f');
                                    break;
                                case (int)'n':
                                    sb.Append('\n');
                                    break;
                                case (int)'r':
                                    sb.Append('\r');
                                    break;
                                case (int)'t':
                                    sb.Append('\t');
                                    break;
                                case (int)'u':
                                    {
                                        StringBuilder sb2 = new StringBuilder();
                                        sb2.Append(ReadHex(reader));
                                        sb2.Append(ReadHex(reader));
                                        sb2.Append(ReadHex(reader));
                                        sb2.Append(ReadHex(reader));
                                        sb.Append((char)Int32.Parse(sb2.ToString(), System.Globalization.NumberStyles.AllowHexSpecifier));
                                    }
                                    break;
                            }
                        }
                        break;
                    default:
                        sb.Append((char)c);
                        break;
                }
            }
        }

        private static char ReadHex(TextReader reader)
        {
            int c = reader.Read();
            if (c == -1)
                throw new InvalidOperationException("JSON Parse Error");
            if ((c >= 'a' && c <= 'f') || (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))
                return (char)c;
            else
                throw new InvalidOperationException("JSON Parse Error");
        }

        private static Datum ParseNumber(TextReader reader, char first)
        {
            bool fp = first == '.';
            StringBuilder sb = new StringBuilder();
            sb.Append(first);
            while (true)
            {
                int c = reader.Peek();
                switch (c)
                {
                    case (int)'-':
                    case (int)'+':
                    case (int)'0':
                    case (int)'1':
                    case (int)'2':
                    case (int)'3':
                    case (int)'4':
                    case (int)'5':
                    case (int)'6':
                    case (int)'7':
                    case (int)'8':
                    case (int)'9':
                        reader.Read();
                        sb.Append((char)c);
                        break;
                    case (int)'.':
                    case (int)'e':
                    case (int)'E':
                        fp = true;
                        reader.Read();
                        sb.Append((char)c);
                        break;
                    case -1:
                    default:
                        if (fp)
                            return new Datum() { type = Datum.DatumType.R_NUM, r_num = double.Parse(sb.ToString(), CultureInfo.InvariantCulture) };
                        else
                            return new Datum() { type = Datum.DatumType.R_NUM, r_num = int.Parse(sb.ToString(), CultureInfo.InvariantCulture) };
                }
            }
        }

        private static Datum ParseArray(TextReader reader, ref int depthCount)
        {
            Datum array = new Datum() { type = Datum.DatumType.R_ARRAY };

            bool reading = true;
            while (reading)
            {
                int p = reader.Peek();
                switch (p)
                {
                    case -1:
                        throw new InvalidOperationException("JSON Parse Error");
                    case (int)' ':
                    case (int)'\t':
                    case (int)'\n':
                    case (int)'\r':
                        reader.Read();
                        continue;
                    case (int)']':
                        reader.Read();
                        reading = false;
                        break;
                    default:
                        {
                            Datum obj = Parse(reader, ref depthCount);
                            array.r_array.Add(obj);
                            bool searchingForComma = true;
                            while (searchingForComma)
                            {
                                int c = reader.Read();
                                switch (c)
                                {
                                    case -1:
                                        throw new InvalidOperationException("JSON Parse Error");
                                    case (int)' ':
                                    case (int)'\t':
                                    case (int)'\n':
                                    case (int)'\r':
                                        continue;
                                    case (int)',':
                                        searchingForComma = false;
                                        break;
                                    case (int)']':
                                        searchingForComma = false;
                                        reading = false;
                                        break;
                                    default:
                                        throw new InvalidOperationException("JSON Parse Error");
                                }
                            }
                        }
                        break;
                }
            }

            return array;
        }

        private static Datum ParseObject(TextReader reader, ref int depthCount)
        {
            Datum retval = new Datum() { type = Datum.DatumType.R_OBJECT };

            while (true)
            {
                int c = reader.Read();
                switch (c)
                {
                    case -1:
                        throw new InvalidOperationException("JSON Parse Error");
                    case (int)' ':
                    case (int)'\t':
                    case (int)'\n':
                    case (int)'\r':
                        continue;
                    case (int)'"':
                        {
                            Datum key = ParseString(reader);
                            bool readForColon = true;
                            while (readForColon)
                            {
                                int c2 = reader.Read();
                                switch (c2)
                                {
                                    case -1:
                                        throw new InvalidOperationException("JSON Parse Error");
                                    case (int)' ':
                                    case (int)'\t':
                                    case (int)'\n':
                                    case (int)'\r':
                                        continue;
                                    case (int)':':
                                        readForColon = false;
                                        break;
                                    default:
                                        throw new InvalidOperationException("JSON Parse Error");
                                }
                            }
                            
                            var value = Parse(reader, ref depthCount);
                            retval.r_object.Add(new Datum.AssocPair() {
                                key = key.r_str,
                                val = value
                            });

                            bool readForComma = true;
                            while (readForComma)
                            {
                                int c2 = reader.Read();
                                switch (c2)
                                {
                                    case -1:
                                        throw new InvalidOperationException("JSON Parse Error");
                                    case (int)' ':
                                    case (int)'\t':
                                    case (int)'\n':
                                    case (int)'\r':
                                        continue;
                                    case (int)',':
                                        readForComma = false;
                                        break;
                                    case (int)'}':
                                        return retval;
                                    default:
                                        throw new InvalidOperationException("JSON Parse Error");
                                }
                            }
                        }
                        break;
                    case (int)'}':
                        return retval;
                    default:
                        throw new InvalidOperationException("JSON Parse Error");
                }
            }
        }
    }
}
