using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DesignerReorder
{
    internal sealed class ControlInfo
    {
        public string ControlName { get; set; } = string.Empty;
        public int LocationY { get; set; } = int.MaxValue;
        public int LocationX { get; set; } = int.MaxValue;
        public bool Table { get; set; } = false;
        public string Creation { get; set; } = string.Empty;
        public string Suspendlayout { get; set; } = string.Empty;
        public string BeginInit { get; set; } = string.Empty;
        public string EndInit { get; set; } = string.Empty;
        public string ResumeLayout { get; set; } = string.Empty;
        public string PerformLayout { get; set; } = string.Empty;
        public string Declaration { get; set; } = string.Empty;
        public string ParentName { get; set; } = string.Empty;
        public List<string> Properties { get; } = new List<string>();
        public List<ControlInfo> Children { get; set; } = new List<ControlInfo>();
    }

    internal static class Program
    {
        private static readonly Regex ClassNameRegex = new(
            @"^\s*(?:\[(?:[^\]]*)\]\s*)*(?:public|internal|protected|private|sealed|static|abstract|partial|unsafe|new|\s+)*\bclass\s+(?<name>[A-Za-z_]\w*)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
        // Regex to build the control tree
        private static readonly Regex DeclarationRegex = new(@"^\s*(?:private|internal|protected|public)\s+[\w\.<>,\s]+\s+(?<name>\w+)\s*;\s*$", RegexOptions.Compiled);
        private static readonly Regex ControlsAddRegex = new(@"(?<parent>[\w\.]+)\.Controls\.Add\s*\(\s*this\.(?<child>\w+)\s*\)\s*;", RegexOptions.Compiled);
        private static readonly Regex ColumnHeaderRegex = new(@"^\s*this\.(?<parent>\w+)\.Columns\.AddRange\s*\(\s*new\s+(?:System\.Windows\.Forms\.)?(?:ColumnHeader|DataGridViewColumn)\[\]\s*\{", RegexOptions.Compiled);
        private static readonly Regex ToolStripItemRegex = new(@"^\s*this\.(?<parent>\w+)\.(?:Items|DropDownItems)\.AddRange\s*\(\s*new\s+(?:System\.Windows\.Forms\.)?ToolStripItem\[\]\s*\{", RegexOptions.Compiled);
        private static readonly Regex ChildRegex = new(@"^\s*this\.(?<child>\w+)", RegexOptions.Compiled);
        // Regex to find control's properties
        private static readonly Regex CreationRegex = new(@"^\s*(?:this\.)?(?<name>\w*)\s*=\s*new\s+[\w\.<>,\s]+\(", RegexOptions.Compiled);
        private static readonly Regex BeginInitRegex = new(@"this\.(?<name>\w+)(?=[^;]*BeginInit\s*\(\s*\))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex EndInitRegex = new(@"this\.(?<name>\w+)(?=[^;]*EndInit\s*\(\s*\))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SuspendLayoutRegex = new(@"^\s*(?:this\.(?<name>\w+)|(?<name>this|\w+))\.SuspendLayout\s*\(\s*\)\s*;", RegexOptions.Compiled);
        private static readonly Regex ResumeLayoutRegex = new(@"^\s*(?:this\.(?<name>\w+)|(?<name>this|\w+))\.ResumeLayout\s*(?:\(\s*[\w, ]*\))?\s*;", RegexOptions.Compiled);
        private static readonly Regex PerformLayoutRegex = new(@"^\s*(?:this\.(?<name>\w+)|(?<name>this|\w+))\.PerformLayout\s*\(\s*\)\s*;", RegexOptions.Compiled);
        private static readonly Regex ControlCommentRegex = new(@"^\s*//\s*(?<name>[A-Za-z_]\w*)\s*$", RegexOptions.Compiled);
        private static readonly Regex LocationRegex = new(@"(?<name>[\w\.]+)\.Location\s*=\s*new\s+System\.Drawing\.Point\s*\(\s*(?<x>-?\d+)\s*,\s*(?<y>-?\d+)\s*\)\s*;", RegexOptions.Compiled);

        private static int YTolerance = 7;

        private static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.WriteLine("Usage: DesignerReorder <path-to-Form.Designer.cs>");
                return 1;
            }

            var path = args[0];
            if (!File.Exists(path))
            {
                Console.WriteLine($"File not found: {path}");
                return 1;
            }

            var originalLines = File.ReadAllLines(path).ToList();

            try
            {
                var resultLines = DesignerReorderFile(originalLines);
                // backup
                var bak = path + ".bak";
                File.Copy(path, bak, overwrite: true);
                File.WriteAllLines(path, resultLines, System.Text.Encoding.UTF8);
                Console.WriteLine($"Reordered file written. Backup saved to: {bak}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                return 2;
            }
        }

        private static List<string> DesignerReorderFile(List<string> lines)
        {
            List<string> BodyStart = new List<string>();
            List<string> BodyMid = new List<string>();
            List<string> BodyEnd = new List<string>();

            BodyMid.Add("");
            BodyMid.Add("        }");
            BodyMid.Add("");
            BodyMid.Add("        #endregion");
            BodyMid.Add("");
            BodyEnd.Add("    }");
            BodyEnd.Add("}");

            string szClassName = string.Empty;
            // collect controls declarations (entire file)
            var controlsDeclarations = new Dictionary<string, (int index, string text)>(StringComparer.Ordinal);
            for (int i = 0; i < lines.Count; i++)
            {
                var m = DeclarationRegex.Match(lines[i]);
                if (m.Success)
                {
                    var name = m.Groups["name"].Value;
                    controlsDeclarations[name] = (i, lines[i]);
                }
                m = ClassNameRegex.Match(lines[i]);
                if (m.Success)
                {
                    szClassName = m.Groups["name"].Value;
                }
            }
            // Here we have all internal controls.

            // Detect all the controls parent using the Controls.Add lines (entire file)
            var controlsParents = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < lines.Count; i++)
            {
                var m = ControlsAddRegex.Match(lines[i]);
                if (m.Success)
                {
                    var parent = m.Groups["parent"].Value;
                    if (parent.StartsWith("this."))
                    {
                        parent = parent.Substring(5);
                    }
                    var child = m.Groups["child"].Value;
                    controlsParents[child] = parent;
                }
                // Special case of Forms.ColumnHeader[]
                m = ColumnHeaderRegex.Match(lines[i]);
                if (m.Success)
                {
                    var parent = m.Groups["parent"].Value;
                    i++;
                    while (true)
                    {
                        var n = ChildRegex.Match(lines[i]);
                        if (n.Success)
                        {
                            var child = n.Groups["child"].Value;
                            controlsParents[child] = parent;
                        }
                        if (lines[i].Contains("});"))
                            break;
                        i++;
                    }
                }
                // Special case of Forms.MenuItem[]
                m = ToolStripItemRegex.Match(lines[i]);
                if (m.Success)
                {
                    var parent = m.Groups["parent"].Value;
                    i++;
                    while (true)
                    {
                        var n = ChildRegex.Match(lines[i]);
                        if (n.Success)
                        {
                            var child = n.Groups["child"].Value;
                            controlsParents[child] = parent;
                        }
                        if (lines[i].Contains("});"))
                            break;
                        i++;
                    }
                }
            }

            // Add all controls not already added as root controls (parent = null)
            foreach (var control in controlsDeclarations.Keys)
            {
                if (!controlsParents.ContainsKey(control))
                {
                    controlsParents[control] = null;
                }
            }

            // Add the main form as last root control
            controlsParents["this"] = null;

            // Build the control tree and sort it by Parent, LocationY and LocationX
            var controlTree = BuildControlTree(lines, controlsDeclarations, controlsParents);

            ControlInfo LastControl = null;
            bool bStartedBody = false;
            // Fill controlInfo properties from the lines
            for (int i = 0; i < lines.Count; i++)
            {
                var m = CreationRegex.Match(lines[i]);
                if (m.Success)
                {
                    // Caso speciale per "this.components" che ha solo il creationù
                    if (m.Groups["name"].Value == "components")
                    {
                        controlTree.Insert(0, new ControlInfo() { Creation = lines[i] });
                        continue;
                    }
                    else
                    {
                        var controlInfo = GetControlInfo(controlTree, m.Groups["name"].Value);
                        if (controlInfo != null)
                            controlInfo.Creation = lines[i];
                        bStartedBody = true;
                    }
                }
                if (!bStartedBody)
                    BodyStart.Add(lines[i]);
                m = BeginInitRegex.Match(lines[i]);
                if (m.Success)
                {
                    var controlInfo = GetControlInfo(controlTree, m.Groups["name"].Value);
                    controlInfo.BeginInit = lines[i];
                }
                m = SuspendLayoutRegex.Match(lines[i]);
                if (m.Success)
                {
                    var controlInfo = GetControlInfo(controlTree, m.Groups["name"].Value);
                    controlInfo.Suspendlayout = lines[i];
                }
                m = EndInitRegex.Match(lines[i]);
                if (m.Success)
                {
                    var controlInfo = GetControlInfo(controlTree, m.Groups["name"].Value);
                    controlInfo.EndInit = lines[i];
                    LastControl = null;
                }
                m = ResumeLayoutRegex.Match(lines[i]);
                if (m.Success)
                {
                    var controlInfo = GetControlInfo(controlTree, m.Groups["name"].Value);
                    controlInfo.ResumeLayout = lines[i];
                    LastControl = null;
                }
                m = PerformLayoutRegex.Match(lines[i]);
                if (m.Success)
                {
                    var controlInfo = GetControlInfo(controlTree, m.Groups["name"].Value);
                    controlInfo.PerformLayout = lines[i];
                    LastControl = null;
                }
                if (bStartedBody)
                {
                    m = ControlCommentRegex.Match(lines[i]);
                    if (m.Success)
                    {
                        string szName = m.Groups["name"].Value;
                        if (m.Groups["name"].Value == szClassName)
                            szName = "this";
                        LastControl = GetControlInfo(controlTree, szName);
                        LastControl.Properties.Add(lines[i]);
                    }
                    else if (LastControl != null)
                    {
                        // Aggiungo anche i Control.Add che saranno filtrati e riordinati in seguito
                        LastControl.Properties.Add(lines[i]);
                    }
                    m = ColumnHeaderRegex.Match(lines[i]);
                    if (m.Success)
                    {
                        var controlInfo = GetControlInfo(controlTree, m.Groups["parent"].Value);
                        controlInfo.Table = true;
                    }
                    m = LocationRegex.Match(lines[i]);
                    if (m.Success)
                    {
                        var controlInfo = GetControlInfo(controlTree, m.Groups["name"].Value);
                        controlInfo.LocationX = int.Parse(m.Groups["x"].Value);
                        controlInfo.LocationY = int.Parse(m.Groups["y"].Value);
                    }
                }
            }

            SortAllControlInfo(controlTree, true);

            List<string> Output = new List<string>();
            // Recreate designer file with the new order
            Output.AddRange(BodyStart);
            // Add creation of controls in the order of the control tree, except for the main form (this) which is already created in the designer file
            foreach (var control in controlTree)
            {
                List<string> strs = GetCreationStrings(control);
                if (strs.Count > 0)
                    Output.AddRange(strs);
            }
            // Add BeginInit and SuspendLayout in the order of the control tree
            foreach (var control in controlTree)
            {
                List<string> strs = GetBeginStrings(control);
                if (strs.Count > 0)
                    Output.AddRange(strs);
            }
            Output.Add("            // ");
            // Add properties of controls in the order of the control tree (and also the Controls.Add lines)
            foreach (var control in controlTree)
            {
                List<string> strs = GetPropertiesStrings(control);
                if (strs.Count > 0)
                    Output.AddRange(strs);
            }
            // Add EndInit, ResumeLayout and PerformLayout in the order of the control tree
            foreach (var control in controlTree)
            {
                List<string> strs = GetEndStrings(control);
                if (strs.Count > 0)
                    Output.AddRange(strs);
            }
            Output.AddRange(BodyMid);
            // Add controls declarations in the order of the control tree, except for the main form (this) which is already declared in the designer file
            foreach (var control in controlTree)
            {
                List<string> strs = GetDeclarationStrings(control);
                if (strs.Count > 0)
                    Output.AddRange(strs);
            }
            Output.AddRange(BodyEnd);

            return Output;
        }

        // return a list of ControlInfo objects representing the control tree
        private static List<ControlInfo> BuildControlTree(List<string> lines, Dictionary<string, (int index, string text)> controlsDeclarations, Dictionary<string, string> controlsParents)
        {
            var controls = FindAllChildren(lines, null, controlsDeclarations, controlsParents);
            return controls;
        }

        private static List<ControlInfo> FindAllChildren(List<string> lines, string parent, Dictionary<string, (int index, string text)> controlsDeclarations, Dictionary<string, string> controlsParents)
        {
            // Find all controls that have the specified parent
            var controls = new List<ControlInfo>();
            foreach (var control in controlsParents.Keys)
            {
                if (controlsParents[control] == parent)
                {
                    var controlInfo = new ControlInfo
                    {
                        ControlName = control,
                        ParentName = parent
                    };
                    controlInfo.Declaration = controlsDeclarations.TryGetValue(control, out var d) ? d.text : string.Empty;
                    controlInfo.Children = FindAllChildren(lines, control, controlsDeclarations, controlsParents);
                    controls.Add(controlInfo);
                }
            }
            //controls.Sort(new ControlInfoComparer(YTolerance));
            return controls;
        }

        //internal sealed class ControlInfoComparer : IComparer<ControlInfo>
        //{
        //    private readonly int _yTolerance;

        //    public ControlInfoComparer(int yTolerance = 10) => _yTolerance = Math.Max(0, yTolerance);

        //    public int Compare(ControlInfo? x, ControlInfo? y)
        //    {
        //        if (ReferenceEquals(x, y)) return 0;
        //        if (x is null) return -1;
        //        if (y is null) return 1;

        //        // Compare by Y with tolerance
        //        int dy = x.LocationY - y.LocationY;
        //        if (Math.Abs(dy) > _yTolerance)
        //            return dy; // positive = x below y => greater

        //        // Y considered equal within tolerance -> compare by X ascending
        //        int dx = x.LocationX - y.LocationX;
        //        if (dx != 0)
        //            return dx;

        //        // Final stable tiebreaker: control name
        //        return string.CompareOrdinal(x.ControlName, y.ControlName);
        //    }
        //}

        private static ControlInfo GetControlInfo(List<ControlInfo> controls, string controlName)
        {
            if (controlName.StartsWith("this."))
            {
                controlName = controlName.Substring(5);
            }
            foreach (var control in controls)
            {
                if (control.ControlName == controlName)
                {
                    return control;
                }
                var child = GetControlInfo(control.Children, controlName);
                if (child != null)
                {
                    return child;
                }
            }
            return null;
        }

        private static void SortAllControlInfo(List<ControlInfo> controls, bool bStraightOrder)
        {
            // Sort the controls by LocationY and LocationX with a tolerance for Y
            controls.Sort((a, b) =>
            {
                if (Math.Abs(a.LocationY - b.LocationY) <= YTolerance)
                {
                    return a.LocationX.CompareTo(b.LocationX);
                }
                return a.LocationY.CompareTo(b.LocationY);
            });
            if (!bStraightOrder)
                controls.Reverse();
            foreach (var control in controls)
            {
                if (control.Children.Count > 1)
                    SortAllControlInfo(control.Children, control.ControlName == "this" || control.Table);
            }
        }
        private static List<string> GetCreationStrings(ControlInfo control)
        {
            List<string> strs = new List<string>();
            if (!string.IsNullOrEmpty(control.Creation) && control.ControlName != "this")
            {
                strs.Add(control.Creation);
            }
            foreach (var child in control.Children)
            {
                strs.AddRange(GetCreationStrings(child));
            }
            return strs;
        }

        private static List<string> GetBeginStrings(ControlInfo control)
        {
            List<string> strs = new List<string>();
            if (control.ControlName != "this")
            {
                if (!string.IsNullOrEmpty(control.BeginInit))
                {
                    strs.Add(control.BeginInit);
                }
                if (!string.IsNullOrEmpty(control.Suspendlayout))
                {
                    strs.Add(control.Suspendlayout);
                }
            }
            foreach (var child in control.Children)
            {
                strs.AddRange(GetBeginStrings(child));
            }
            if (control.ControlName == "this")
            {
                if (!string.IsNullOrEmpty(control.BeginInit))
                {
                    strs.Add(control.BeginInit);
                }
                if (!string.IsNullOrEmpty(control.Suspendlayout))
                {
                    strs.Add(control.Suspendlayout);
                }
            }
            return strs;
        }

        private static List<string> GetPropertiesStrings(ControlInfo control)
        {
            List<string> strs = new List<string>();
            if (control.ControlName != "this")
            {
                strs.AddRange(GetSingleControlPropertiesStrings(control));
            }
            foreach (var child in control.Children)
            {
                strs.AddRange(GetPropertiesStrings(child));
            }
            if (control.ControlName == "this")
            {
                strs.AddRange(GetSingleControlPropertiesStrings(control));
            }
            return strs;
        }

        private static List<string> GetSingleControlPropertiesStrings(ControlInfo control)
        {
            bool bControlAdded = false;
            List<string> strs = new List<string>();
            foreach (var prop in control.Properties)
            {
                var m = ControlsAddRegex.Match(prop);
                if (!m.Success)
                    strs.Add(prop);
                else if (!bControlAdded)
                {
                    foreach (var child in control.Children)
                    {
                        if (control.ControlName == "this")
                            strs.Add($"            this.Controls.Add(this.{child.ControlName});");
                        else
                            strs.Add($"            this.{control.ControlName}.Controls.Add(this.{child.ControlName});");
                    }
                    bControlAdded = true;
                }
            }
            return strs;
        }

        private static List<string> GetEndStrings(ControlInfo control)
        {
            List<string> strs = new List<string>();
            if (control.ControlName != "this")
            {
                if (!string.IsNullOrEmpty(control.EndInit))
                {
                    strs.Add(control.EndInit);
                }
                if (!string.IsNullOrEmpty(control.ResumeLayout))
                {
                    strs.Add(control.ResumeLayout);
                }
                if (!string.IsNullOrEmpty(control.PerformLayout))
                {
                    strs.Add(control.PerformLayout);
                }
            }
            foreach (var child in control.Children)
            {
                strs.AddRange(GetEndStrings(child));
            }
            if (control.ControlName == "this")
            {
                if (!string.IsNullOrEmpty(control.EndInit))
                {
                    strs.Add(control.EndInit);
                }
                if (!string.IsNullOrEmpty(control.ResumeLayout))
                {
                    strs.Add(control.ResumeLayout);
                }
                if (!string.IsNullOrEmpty(control.PerformLayout))
                {
                    strs.Add(control.PerformLayout);
                }
            }
            return strs;
        }

        private static List<string> GetDeclarationStrings(ControlInfo control)
        {
            List<string> strs = new List<string>();
            if (!string.IsNullOrEmpty(control.Declaration) && control.ControlName != "this")
            {
                strs.Add(control.Declaration);
            }
            foreach (var child in control.Children)
            {
                strs.AddRange(GetDeclarationStrings(child));
            }
            return strs;
        }
    }
}