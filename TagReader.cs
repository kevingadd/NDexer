﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using Squared.Task;

namespace Ndexer {
    public struct Tag {
        public string Name;
        public string Kind;
        public string Language;
        public string SourceFile;
        public int LineNumber;
        public string Context;

        public override string ToString() {
            var result = new StringBuilder();
            result.Append(SourceFile);
            result.Append("@");
            result.Append(LineNumber);
            result.Append(" '");
            result.Append(Name);
            result.Append("' ");
            if (Kind != null) {
                result.Append("kind='");
                result.Append(Kind);
                result.Append("' ");
            }
            if (Language != null) {
                result.Append("lang='");
                result.Append(Language);
                result.Append("' ");
            }
            if (Context != null) {
                result.Append("ctxt='");
                result.Append(Context);
                result.Append("' ");
            }
            return result.ToString();
        }
    }

    public static class TagReader {
        public static bool ReadTag (string line, out Tag tag) {
            tag = new Tag();
            var sep = new char[] { '\t' };
            var header = "\t/^";
            var sentinel = ";\"\t";

            int startPos = line.LastIndexOf(sentinel);
            int nextPos = line.IndexOf('\t');
            int splitPos = line.IndexOf(header);
            if (splitPos == -1)
                splitPos = startPos;
            if (startPos == -1)
                return false;

            tag.Name = line.Substring(0, nextPos);

            startPos += sentinel.Length;
            int sourceFileLength = (splitPos - nextPos) - 1;
            int extraTabPos = line.IndexOf('\t', nextPos + 1);
            if (extraTabPos < splitPos)
                sourceFileLength = (extraTabPos - nextPos) - 1;

            tag.SourceFile = line.Substring(nextPos + 1, sourceFileLength);

            while (startPos > 0) {
                nextPos = line.IndexOf('\t', startPos) + 1;
                if (nextPos < startPos)
                    nextPos = line.Length + 1;

                splitPos = line.IndexOf(':', startPos);
                if (splitPos < nextPos) {
                    string key = line.Substring(startPos, splitPos - startPos);
                    string value = line.Substring(splitPos + 1, nextPos - splitPos - 2);
                    switch (key) {
                        case "kind":
                            tag.Kind = value;
                            break;
                        case "line":
                            tag.LineNumber = int.Parse(value);
                            break;
                        case "language":
                            tag.Language = value;
                            break;
                        case "function":
                        case "class":
                        case "namespace":
                            tag.Context = value;
                            break;
                    }
                }

                if (nextPos < line.Length)
                    startPos = nextPos;
                else
                    break;
            }

            return true;
        }
    }
}
