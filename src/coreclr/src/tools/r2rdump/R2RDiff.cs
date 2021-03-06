// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;

using ILCompiler.Reflection.ReadyToRun;
using Internal.Runtime;

namespace R2RDump
{
    /// <summary>
    /// Helper class for diffing a pair of R2R images.
    /// </summary>
    class R2RDiff
    {
        /// <summary>
        /// Left dumper to use for the diff
        /// </summary>
        private readonly Dumper _leftDumper;

        /// <summary>
        /// Right dumper to use for the diff
        /// </summary>
        private readonly Dumper _rightDumper;

        /// <summary>
        /// Text writer to use for common output
        /// </summary>
        private readonly TextWriter _writer;

        /// <summary>
        /// Store the left and right file and output writer.
        /// </summary>
        /// <param name="leftDumper">Dumper to use for the left diff output</param>
        /// <param name="rightDumper">Dumper to use for the right diff output</param>
        /// <param name="writer">Writer to use for output common to left / right side</param>
        public R2RDiff(Dumper leftDumper, Dumper rightDumper, TextWriter writer)
        {
            _leftDumper = leftDumper;
            _rightDumper = rightDumper;
            _writer = writer;
        }

        /// <summary>
        /// Public API runs all available diff algorithms in sequence.
        /// </summary>
        public void Run()
        {
            DiffTitle();
            DiffPESections();
            DiffR2RSections();
            DiffR2RMethods();

            Dictionary<string, ReadyToRunMethod> leftMethods = new Dictionary<string, ReadyToRunMethod>(_leftDumper.Reader.Methods
                .Select(method => new KeyValuePair<string, ReadyToRunMethod>(method.SignatureString, method)));
            Dictionary<string, ReadyToRunMethod> rightMethods = new Dictionary<string, ReadyToRunMethod>(_rightDumper.Reader.Methods
                .Select(method => new KeyValuePair<string, ReadyToRunMethod>(method.SignatureString, method)));
            Dictionary<string, MethodPair> commonMethods = new Dictionary<string, MethodPair>(leftMethods
                .Select(kvp => new KeyValuePair<string, MethodPair>(kvp.Key,
                    new MethodPair(kvp.Value, rightMethods.TryGetValue(kvp.Key, out ReadyToRunMethod rightMethod) ? rightMethod : null)))
                .Where(kvp => kvp.Value.RightMethod != null));
            if (_leftDumper.Options.DiffHideSameDisasm)
            {
                commonMethods = new Dictionary<string, MethodPair>(HideMethodsWithSameDisassembly(commonMethods));
            }
            DumpCommonMethods(_leftDumper, commonMethods);
            DumpCommonMethods(_rightDumper, commonMethods);
        }

        /// <summary>
        /// Diff title shows the names of the files being compared and their lengths.
        /// </summary>
        private void DiffTitle()
        {
            _writer.WriteLine($@"Left file:  {_leftDumper.Reader.Filename} ({_leftDumper.Reader.Image.Length} B)");
            _writer.WriteLine($@"Right file: {_rightDumper.Reader.Filename} ({_rightDumper.Reader.Image.Length} B)");
            _writer.WriteLine();
        }

        /// <summary>
        /// Diff raw PE sections.
        /// </summary>
        private void DiffPESections()
        {
            ShowDiff(GetPESectionMap(_leftDumper.Reader), GetPESectionMap(_rightDumper.Reader), "PE sections");
        }

        /// <summary>
        /// Diff R2R header sections.
        /// </summary>
        private void DiffR2RSections()
        {
            ShowDiff(GetR2RSectionMap(_leftDumper.Reader), GetR2RSectionMap(_rightDumper.Reader), "R2R sections");
        }

        /// <summary>
        /// Diff the R2R method maps.
        /// </summary>
        private void DiffR2RMethods()
        {
            ShowDiff(GetR2RMethodMap(_leftDumper.Reader), GetR2RMethodMap(_rightDumper.Reader), "R2R methods");
        }

        /// <summary>
        /// Show a difference summary between the sets of "left objects" and "right objects".
        /// </summary>
        /// <param name="leftObjects">Dictionary of left object sizes keyed by their names</param>
        /// <param name="rightObjects">Dictionary of right object sizes keyed by their names</param>
        /// <param name="diffName">Logical name of the diffing operation to display in the header line</param>
        private void ShowDiff(Dictionary<string, int> leftObjects, Dictionary<string, int> rightObjects, string diffName)
        {
            HashSet<string> allKeys = new HashSet<string>(leftObjects.Keys);
            allKeys.UnionWith(rightObjects.Keys);

            string title = $@" LEFT_SIZE RIGHT_SIZE       DIFF  {diffName} ({allKeys.Count} ELEMENTS)";

            _writer.WriteLine(title);
            _writer.WriteLine(new string('-', title.Length));

            int leftTotal = 0;
            int rightTotal = 0;
            foreach (string key in allKeys)
            {
                int leftSize;
                bool inLeft = leftObjects.TryGetValue(key, out leftSize);
                int rightSize;
                bool inRight = rightObjects.TryGetValue(key, out rightSize);

                leftTotal += leftSize;
                rightTotal += rightSize;

                StringBuilder line = new StringBuilder();
                if (inLeft)
                {
                    line.AppendFormat("{0,10}", leftSize);
                }
                else
                {
                    line.Append(' ', 10);
                }
                if (inRight)
                {
                    line.AppendFormat("{0,11}", rightSize);
                }
                else
                {
                    line.Append(' ', 11);
                }
                if (leftSize != rightSize)
                {
                    line.AppendFormat("{0,11}", rightSize - leftSize);
                }
                else
                {
                    line.Append(' ', 11);
                }
                line.Append("  ");
                line.Append(key);
                _writer.WriteLine(line);
            }
            _writer.WriteLine($@"{leftTotal,10} {rightTotal,10} {(rightTotal - leftTotal),10}  <TOTAL>");

            _writer.WriteLine();
        }

        /// <summary>
        /// Read the PE file section map for a given R2R image.
        /// </summary>
        /// <param name="reader">R2R image to scan</param>
        /// <returns></returns>
        private Dictionary<string, int> GetPESectionMap(ReadyToRunReader reader)
        {
            Dictionary<string, int> sectionMap = new Dictionary<string, int>();

            foreach (SectionHeader sectionHeader in reader.PEReader.PEHeaders.SectionHeaders)
            {
                sectionMap.Add(sectionHeader.Name, sectionHeader.SizeOfRawData);
            }

            return sectionMap;
        }

        /// <summary>
        /// Read the R2R header section map for a given R2R image.
        /// </summary>
        /// <param name="reader">R2R image to scan</param>
        /// <returns></returns>
        private Dictionary<string, int> GetR2RSectionMap(ReadyToRunReader reader)
        {
            Dictionary<string, int> sectionMap = new Dictionary<string, int>();

            foreach (KeyValuePair<ReadyToRunSectionType, ReadyToRunSection> typeAndSection in reader.ReadyToRunHeader.Sections)
            {
                string name = typeAndSection.Key.ToString();
                sectionMap.Add(name, typeAndSection.Value.Size);
            }

            return sectionMap;
        }

        /// <summary>
        /// Read the R2R method map for a given R2R image.
        /// </summary>
        /// <param name="reader">R2R image to scan</param>
        /// <returns></returns>
        private Dictionary<string, int> GetR2RMethodMap(ReadyToRunReader reader)
        {
            Dictionary<string, int> methodMap = new Dictionary<string, int>();

            foreach (ReadyToRunMethod method in reader.Methods)
            {
                int size = method.RuntimeFunctions.Sum(rf => rf.Size);
                methodMap.Add(method.SignatureString, size);
            }

            return methodMap;
        }

        /// <summary>
        /// Dump the subset of methods common to both sides of the diff to the given dumper.
        /// </summary>
        /// <param name="dumper">Output dumper to use</param>
        /// <param name="signatureFilter">Set of common signatures of methods to dump</param>
        private void DumpCommonMethods(Dumper dumper, Dictionary<string, MethodPair> signatureFilter)
        {
            IEnumerable<ReadyToRunMethod> filteredMethods = dumper
                .Reader
                .Methods
                .Where(method => signatureFilter.ContainsKey(method.SignatureString))
                .OrderBy(method => method.SignatureString);

            foreach (ReadyToRunMethod method in filteredMethods)
            {
                dumper.DumpMethod(method);
            }
        }

        /// <summary>
        /// Filter out methods that have identical left / right disassembly.
        /// </summary>
        /// <param name="commonMethods">Enumeration of common methods to filter</param>
        /// <returns>Filtered method enumeration</returns>
        private IEnumerable<KeyValuePair<string, MethodPair>> HideMethodsWithSameDisassembly(IEnumerable<KeyValuePair<string, MethodPair>> commonMethods)
        {
            bool first = true;
            foreach (KeyValuePair<string, MethodPair> commonMethod in commonMethods)
            {
                bool match = (commonMethod.Value.LeftMethod.RuntimeFunctions.Count == commonMethod.Value.RightMethod.RuntimeFunctions.Count);
                if (match)
                {
                    for (int rtfIndex = 0; match && rtfIndex < commonMethod.Value.LeftMethod.RuntimeFunctions.Count; rtfIndex++)
                    {
                        RuntimeFunction leftRuntimeFunction = commonMethod.Value.LeftMethod.RuntimeFunctions[rtfIndex];
                        RuntimeFunction rightRuntimeFunction = commonMethod.Value.RightMethod.RuntimeFunctions[rtfIndex];
                        int leftOffset = 0;
                        int rightOffset = 0;
                        for (; ;)
                        {
                            bool leftAtEnd = (leftOffset >= leftRuntimeFunction.Size);
                            bool rightAtEnd = (rightOffset >= rightRuntimeFunction.Size);
                            if (leftAtEnd || rightAtEnd)
                            {
                                if (!leftAtEnd || !rightAtEnd)
                                {
                                    match = false;
                                }
                                break;
                            }
                            leftOffset += _leftDumper.Disassembler.GetInstruction(leftRuntimeFunction,
                                _leftDumper.Reader.GetOffset(leftRuntimeFunction.StartAddress),
                                leftOffset, out string leftInstruction);
                            rightOffset += _rightDumper.Disassembler.GetInstruction(rightRuntimeFunction,
                                _rightDumper.Reader.GetOffset(rightRuntimeFunction.StartAddress),
                                rightOffset, out string rightInstruction);
                            if (leftInstruction != rightInstruction)
                            {
                                match = false;
                                break;
                            }
                        }
                    }
                }
                if (match)
                {
                    if (first)
                    {
                        _writer.WriteLine("Methods with identical disasssbly skipped in common method diff:");
                        first = false;
                    }
                    _writer.WriteLine(commonMethod.Key);
                }
                else
                {
                    yield return commonMethod;
                }
            }
        }

        struct MethodPair
        {
            public readonly ReadyToRunMethod LeftMethod;
            public readonly ReadyToRunMethod RightMethod;

            public MethodPair(ReadyToRunMethod leftMethod, ReadyToRunMethod rightMethod)
            {
                LeftMethod = leftMethod;
                RightMethod = rightMethod;
            }
        }
    }
}
