using System.Text;
using System.Text.RegularExpressions;

#nullable enable
namespace DesignerReorder;

internal static class Program
{
    private static readonly Regex ClassNameRegex = new Regex("^\\s*(?:\\[(?:[^\\]]*)\\]\\s*)*(?:public|internal|protected|private|sealed|static|abstract|partial|unsafe|new|\\s+)*\\bclass\\s+(?<name>[A-Za-z_]\\w*)\\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex DeclarationRegex = new Regex("^\\s*(?:private|internal|protected|public)\\s+[\\w\\.<>,\\s]+\\s+(?<name>\\w+)\\s*;\\s*$", RegexOptions.Compiled);
    private static readonly Regex ControlsAddRegex = new Regex("(?<parent>[\\w\\.]+)\\.Controls\\.Add\\s*\\(\\s*this\\.(?<child>\\w+)\\s*\\)\\s*;", RegexOptions.Compiled);
    private static readonly Regex ColumnHeaderRegex = new Regex("^\\s*this\\.(?<parent>\\w+)\\.Columns\\.AddRange\\s*\\(\\s*new\\s+(?:System\\.Windows\\.Forms\\.)?(?:ColumnHeader|DataGridViewColumn)\\[\\]\\s*\\{", RegexOptions.Compiled);
    private static readonly Regex ToolStripItemRegex = new Regex("^\\s*this\\.(?<parent>\\w+)\\.(?:Items|DropDownItems)\\.AddRange\\s*\\(\\s*new\\s+(?:System\\.Windows\\.Forms\\.)?ToolStripItem\\[\\]\\s*\\{", RegexOptions.Compiled);
    private static readonly Regex ChildRegex = new Regex("^\\s*this\\.(?<child>\\w+)", RegexOptions.Compiled);
    private static readonly Regex CreationRegex = new Regex("^\\s*(?:this\\.)?(?<name>\\w+)\\s*=\\s*[^\\r\\n;]*?\\bnew\\s+[\\w\\.<>,\\s]+\\(", RegexOptions.Compiled);
    private static readonly Regex BeginInitRegex = new Regex("this\\.(?<name>\\w+)(?=[^;]*BeginInit\\s*\\(\\s*\\))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EndInitRegex = new Regex("this\\.(?<name>\\w+)(?=[^;]*EndInit\\s*\\(\\s*\\))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SuspendLayoutRegex = new Regex("^\\s*(?:this\\.(?<name>\\w+)|(?<name>this|\\w+))\\.SuspendLayout\\s*\\(\\s*\\)\\s*;", RegexOptions.Compiled);
    private static readonly Regex ResumeLayoutRegex = new Regex("^\\s*(?:this\\.(?<name>\\w+)|(?<name>this|\\w+))\\.ResumeLayout\\s*(?:\\(\\s*[\\w, ]*\\))?\\s*;", RegexOptions.Compiled);
    private static readonly Regex PerformLayoutRegex = new Regex("^\\s*(?:this\\.(?<name>\\w+)|(?<name>this|\\w+))\\.PerformLayout\\s*\\(\\s*\\)\\s*;", RegexOptions.Compiled);
    private static readonly Regex ControlCommentRegex = new Regex("^\\s*//\\s*(?<name>[A-Za-z_]\\w*)\\s*$", RegexOptions.Compiled);
    private static readonly Regex LocationRegex = new Regex("(?<name>[\\w\\.]+)\\.Location\\s*=\\s*new\\s+System\\.Drawing\\.Point\\s*\\(\\s*(?<x>-?\\d+)\\s*,\\s*(?<y>-?\\d+)\\s*\\)\\s*;", RegexOptions.Compiled);
    private static int YTolerance = 7;

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.WriteLine("Usage: DesignerReorder <path-to-Form.Designer.cs>");
            return 1;
        }
        string str = args[0];
        if (!File.Exists(str))
        {
            Console.WriteLine("File not found: " + str);
            return 1;
        }
        List<string> list = File.ReadAllLines(str).ToList();
        try
        {
            List<string> contents = Program.DesignerReorderFile(list);
            string destFileName = str + ".bak";
            File.Copy(str, destFileName, true);
            File.WriteAllLines(str, contents, Encoding.UTF8);
            Console.WriteLine("Reordered file written. Backup saved to: " + destFileName);
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
        List<string> header = new List<string>();
        List<string> footer = new List<string>();
        List<string> ending = new List<string>();

        footer.Add("");
        footer.Add("        }");
        footer.Add("");
        footer.Add("        #endregion");
        footer.Add("");

        ending.Add("    }");
        ending.Add("}");

        Match m;
        string szClassName = string.Empty;

        // Get all declarations to get all existing controls
        Dictionary<string, string> controlsDeclarations = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Count; i++)
        {
            m = Program.DeclarationRegex.Match(lines[i]);
            if (m.Success)
            {
                string key = m.Groups["name"].Value;
                controlsDeclarations[key] = lines[i];
            }
            m = Program.ClassNameRegex.Match(lines[i]);
            if (m.Success)
                szClassName = m.Groups["name"].Value;
        }
        // Get all Controls.Add lines to rebuild hierarchy
        Dictionary<string, string> controlsParents = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Count; i++)
        {
            m = Program.ControlsAddRegex.Match(lines[i]);
            if (m.Success)
            {
                string str = m.Groups["parent"].Value;
                if (str.StartsWith("this."))
                    str = str.Substring(5);
                string key = m.Groups["child"].Value;
                controlsParents[key] = str;
            }
            // Special case columns that are children of list
            m = Program.ColumnHeaderRegex.Match(lines[i]);
            if (m.Success)
            {
                string str = m.Groups["parent"].Value;
                i++;
                while (true)
                {
                    m = Program.ChildRegex.Match(lines[i]);
                    if (m.Success)
                    {
                        string key = m.Groups["child"].Value;
                        controlsParents[key] = str;
                    }
                    if (!lines[i].Contains("});"))
                        i++;
                    else
                        break;
                }
            }
            // Special case menu items that are children of menu
            m = Program.ToolStripItemRegex.Match(lines[i]);
            if (m.Success)
            {
                string str = m.Groups["parent"].Value;
                i++;
                while (true)
                {
                    m = Program.ChildRegex.Match(lines[i]);
                    if (m.Success)
                    {
                        string key = m.Groups["child"].Value;
                        controlsParents[key] = str;
                    }
                    if (!lines[i].Contains("});"))
                        i++;
                    else
                        break;
                }
            }
        }
        // Adding standalone controls (like timers that have no parent)
        foreach (string key in controlsDeclarations.Keys)
        {
            if (!controlsParents.ContainsKey(key))
                controlsParents[key] = null;
        }
        // Adding main form/control
        controlsParents["this"] = null;
        List<ControlInfo> controls = Program.BuildControlTree(lines, controlsDeclarations, controlsParents);
        ControlInfo LastControlInfo = null;
        bool bodyStarted = false;
        bool bBodyEnded = false;
        for (int i = 0; i < lines.Count; i++)
        {
            m = Program.CreationRegex.Match(lines[i]);
            if (m.Success)
            {
                if (m.Groups["name"].Value == "components")
                {
                    controls.Insert(0, new ControlInfo()
                    {
                        Creation = lines[i]
                    });
                    continue;
                }
                ControlInfo c = Program.GetControlInfo(controls, m.Groups["name"].Value);
                if (c != null)
                    c.Creation = lines[i];
                bodyStarted = true;
            }
            if (!bodyStarted)
                header.Add(lines[i]);
            m = Program.BeginInitRegex.Match(lines[i]);
            if (m.Success)
                Program.GetControlInfo(controls, m.Groups["name"].Value).BeginInit = lines[i];
            m = Program.SuspendLayoutRegex.Match(lines[i]);
            if (m.Success)
                Program.GetControlInfo(controls, m.Groups["name"].Value).Suspendlayout = lines[i];
            m = Program.EndInitRegex.Match(lines[i]);
            if (m.Success)
                Program.GetControlInfo(controls, m.Groups["name"].Value).EndInit = lines[i];
            m = Program.ResumeLayoutRegex.Match(lines[i]);
            if (m.Success)
                Program.GetControlInfo(controls, m.Groups["name"].Value).ResumeLayout = lines[i];
            m = Program.PerformLayoutRegex.Match(lines[i]);
            if (m.Success)
                Program.GetControlInfo(controls, m.Groups["name"].Value).PerformLayout = lines[i];
            // Properties end at the first End/Resume/PerformLayout line or at the end of InitializeComponent,
            // otherwise the last block (usually the form itself) swallows the rest of the file
            if (bodyStarted && (Program.EndInitRegex.IsMatch(lines[i]) || Program.ResumeLayoutRegex.IsMatch(lines[i]) ||
                                Program.PerformLayoutRegex.IsMatch(lines[i]) || lines[i].Trim() == "}"))
                bBodyEnded = true;
            if (bodyStarted && !bBodyEnded)
            {
                m = Program.ControlCommentRegex.Match(lines[i]);
                if (m.Success)
                {
                    string controlName = m.Groups["name"].Value;
                    if (m.Groups["name"].Value == szClassName)
                        controlName = "this";
                    LastControlInfo = Program.GetControlInfo(controls, controlName);
                    LastControlInfo.Properties.Add(lines[i]);
                }
                else
                    LastControlInfo?.Properties.Add(lines[i]);
                m = Program.ColumnHeaderRegex.Match(lines[i]);
                if (m.Success)
                    Program.GetControlInfo(controls, m.Groups["parent"].Value).Table = true;
                m = Program.LocationRegex.Match(lines[i]);
                if (m.Success)
                {
                    ControlInfo controlInfo3 = Program.GetControlInfo(controls, m.Groups["name"].Value);
                    controlInfo3.LocationX = int.Parse(m.Groups["x"].Value);
                    controlInfo3.LocationY = int.Parse(m.Groups["y"].Value);
                }
            }
        }
        Program.SortAllControlInfo(controls);

        // Now rewrite all the files with controls/properties found in the desired order
        List<string> stringList = new List<string>();
        stringList.AddRange(header);
        // Creation
        foreach (ControlInfo control in controls)
        {
            List<string> creationStrings = Program.GetCreationStrings(control);
            if (creationStrings.Count > 0)
                stringList.AddRange(creationStrings);
        }
        // Begin/Suspendlayout
        foreach (ControlInfo control in controls)
        {
            List<string> beginStrings = Program.GetBeginStrings(control);
            if (beginStrings.Count > 0)
                stringList.AddRange(beginStrings);
        }
        stringList.Add("            // ");
        foreach (ControlInfo control in controls)
        {
            List<string> propertiesStrings = Program.GetPropertiesStrings(control);
            if (propertiesStrings.Count > 0)
                stringList.AddRange(propertiesStrings);
        }
        // End/Resumelayout
        foreach (ControlInfo control in controls)
        {
            List<string> endStrings = Program.GetEndStrings(control);
            if (endStrings.Count > 0)
                stringList.AddRange(endStrings);
        }
        stringList.AddRange(footer);
        // Declarations
        foreach (ControlInfo control in controls)
        {
            List<string> declarationStrings = Program.GetDeclarationStrings(control);
            if (declarationStrings.Count > 0)
                stringList.AddRange(declarationStrings);
        }
        stringList.AddRange(ending);
        return stringList;
    }

    private static List<ControlInfo> BuildControlTree(List<string> lines, Dictionary<string, string> controlsDeclarations, Dictionary<string, string> controlsParents)
    {
        return Program.FindAllChildren(lines, null, controlsDeclarations, controlsParents);
    }

    private static List<ControlInfo> FindAllChildren(List<string> lines, string parent, Dictionary<string, string> controlsDeclarations, Dictionary<string, string> controlsParents)
    {
        List<ControlInfo> allChildren = new List<ControlInfo>();
        foreach (string key in controlsParents.Keys)
        {
            if (controlsParents[key] == parent)
            {
                ControlInfo controlInfo = new ControlInfo()
                {
                    ControlName = key,
                    ParentName = parent
                };
                controlInfo.Declaration = controlsDeclarations.ContainsKey(key) ? controlsDeclarations[key] : "";
                controlInfo.Children = Program.FindAllChildren(lines, key, controlsDeclarations, controlsParents);
                allChildren.Add(controlInfo);
            }
        }
        return allChildren;
    }

    private static ControlInfo GetControlInfo(List<ControlInfo> controls, string controlName)
    {
        if (controlName.StartsWith("this."))
            controlName = controlName.Substring(5);
        foreach (ControlInfo control in controls)
        {
            if (control.ControlName == controlName)
                return control;
            ControlInfo controlInfo = Program.GetControlInfo(control.Children, controlName);
            if (controlInfo != null)
                return controlInfo;
        }
        return null;
    }

    private static void SortAllControlInfo(List<ControlInfo> controls)
    {
        // Group controls into rows: a row starts at its topmost control and includes all controls
        // within YTolerance from it; each row is then ordered right to left, so that controls on the
        // right are added first and drawn on top of the ones on their left
        List<ControlInfo> aSortedByY = controls.OrderBy(c => c.LocationY).ToList();
        controls.Clear();
        int i = 0;
        int n = aSortedByY.Count;
        while (i < n)
        {
            long nRowY = aSortedByY[i].LocationY;
            List<ControlInfo> aRow = new List<ControlInfo>();
            while (i < n && aSortedByY[i].LocationY - nRowY <= Program.YTolerance)
                aRow.Add(aSortedByY[i++]);
            controls.AddRange(aRow.OrderByDescending(c => c.LocationX));
        }
        foreach (ControlInfo control in controls)
        {
            if (control.Children.Count > 0)
                Program.SortAllControlInfo(control.Children);
        }
    }

    private static List<string> GetCreationStrings(ControlInfo control)
    {
        List<string> creationStrings = new List<string>();
        if (!string.IsNullOrEmpty(control.Creation) && control.ControlName != "this")
            creationStrings.Add(control.Creation);
        foreach (ControlInfo child in control.Children)
            creationStrings.AddRange(Program.GetCreationStrings(child));
        return creationStrings;
    }

    private static List<string> GetBeginStrings(ControlInfo control)
    {
        List<string> beginStrings = new List<string>();
        if (control.ControlName != "this")
        {
            if (!string.IsNullOrEmpty(control.BeginInit))
                beginStrings.Add(control.BeginInit);
            if (!string.IsNullOrEmpty(control.Suspendlayout))
                beginStrings.Add(control.Suspendlayout);
        }
        foreach (ControlInfo child in control.Children)
            beginStrings.AddRange(Program.GetBeginStrings(child));
        if (control.ControlName == "this")
        {
            if (!string.IsNullOrEmpty(control.BeginInit))
                beginStrings.Add(control.BeginInit);
            if (!string.IsNullOrEmpty(control.Suspendlayout))
                beginStrings.Add(control.Suspendlayout);
        }
        return beginStrings;
    }

    private static List<string> GetPropertiesStrings(ControlInfo control)
    {
        List<string> propertiesStrings = new List<string>();
        if (control.ControlName != "this")
            propertiesStrings.AddRange(Program.GetSingleControlPropertiesStrings(control));
        foreach (ControlInfo child in control.Children)
            propertiesStrings.AddRange(Program.GetPropertiesStrings(child));
        if (control.ControlName == "this")
            propertiesStrings.AddRange(Program.GetSingleControlPropertiesStrings(control));
        return propertiesStrings;
    }

    private static List<string> GetSingleControlPropertiesStrings(ControlInfo control)
    {
        bool flag = false;
        List<string> propertiesStrings = new List<string>();
        foreach (string property in control.Properties)
        {
            if (!Program.ControlsAddRegex.Match(property).Success)
                propertiesStrings.Add(property);
            else if (!flag)
            {
                foreach (ControlInfo child in control.Children)
                {
                    if (control.ControlName == "this")
                        propertiesStrings.Add($"            this.Controls.Add(this.{child.ControlName});");
                    else
                        propertiesStrings.Add($"            this.{control.ControlName}.Controls.Add(this.{child.ControlName});");
                }
                flag = true;
            }
        }
        return propertiesStrings;
    }

    private static List<string> GetEndStrings(ControlInfo control)
    {
        List<string> endStrings = new List<string>();
        if (control.ControlName != "this")
        {
            if (!string.IsNullOrEmpty(control.EndInit))
                endStrings.Add(control.EndInit);
            if (!string.IsNullOrEmpty(control.ResumeLayout))
                endStrings.Add(control.ResumeLayout);
            if (!string.IsNullOrEmpty(control.PerformLayout))
                endStrings.Add(control.PerformLayout);
        }
        foreach (ControlInfo child in control.Children)
            endStrings.AddRange(Program.GetEndStrings(child));
        if (control.ControlName == "this")
        {
            if (!string.IsNullOrEmpty(control.EndInit))
                endStrings.Add(control.EndInit);
            if (!string.IsNullOrEmpty(control.ResumeLayout))
                endStrings.Add(control.ResumeLayout);
            if (!string.IsNullOrEmpty(control.PerformLayout))
                endStrings.Add(control.PerformLayout);
        }
        return endStrings;
    }

    private static List<string> GetDeclarationStrings(ControlInfo control)
    {
        List<string> declarationStrings = new List<string>();
        if (!string.IsNullOrEmpty(control.Declaration) && control.ControlName != "this")
            declarationStrings.Add(control.Declaration);
        foreach (ControlInfo child in control.Children)
            declarationStrings.AddRange(Program.GetDeclarationStrings(child));
        return declarationStrings;
    }
}

internal class ControlInfo
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
