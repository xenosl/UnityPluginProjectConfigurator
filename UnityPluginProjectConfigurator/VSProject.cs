﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace ShuHai.UnityPluginProjectConfigurator
{
    using XmlElement = ProjectElement;
    using XmlPropertyGroup = ProjectPropertyGroupElement;
    using XmlProperty = ProjectPropertyElement;
    using XmlItemGroup = ProjectItemGroupElement;
    using XmlItem = ProjectItemElement;
    using XmlImport = ProjectImportElement;
    using StringPair = KeyValuePair<string, string>;

    public sealed class VSProject : IDisposable
    {
        public readonly Project MSBuildProject;

        public string FilePath => MSBuildProject.FullPath;
        public string DirectoryPath => MSBuildProject.DirectoryPath;

        public Guid Guid { get; private set; }
        public string Name { get; private set; }

        #region Initialization

        private VSProject(string path)
            : this(ProjectCollection.GlobalProjectCollection, path) { }

        private VSProject(ProjectCollection msBuildProjectCollection, string path)
        {
            MSBuildProject = msBuildProjectCollection.LoadProject(path);
            Initialize();
        }

        private VSProject(Project msBuildProject)
        {
            MSBuildProject = msBuildProject ?? throw new ArgumentNullException(nameof(msBuildProject));
            Initialize();
        }

        private void Initialize()
        {
            Guid = new Guid(MSBuildProject.GetProperty("ProjectGuid").EvaluatedValue);
            Name = Path.GetFileNameWithoutExtension(MSBuildProject.FullPath);

            InitializePropertyGroups();
            InitializeItemGroups();
            InitializeImports();
        }

        #endregion Initialization

        #region Deinitialization

        public void Dispose()
        {
            if (disposed)
                return;

            Deinitialize();
            MSBuildProject.ProjectCollection.UnloadProject(MSBuildProject);

            disposed = true;
        }

        private bool disposed;

        private void Deinitialize()
        {
            MSBuildToolsImport = null;
            DefaultPropertyGroup = null;
            Name = null;
            Guid = default(Guid);
        }

        #endregion Deinitialization

        #region Xml

        public ProjectRootElement Xml => MSBuildProject.Xml;

        #region Properties

        public bool FindProperty(Func<XmlProperty, bool> predicate,
            out XmlPropertyGroup propertyGroup, out XmlProperty property)
        {
            XmlProperty propertyForSearch = null;
            propertyGroup = Xml.PropertyGroups.FirstOrDefault(g =>
            {
                propertyForSearch = g.Properties.FirstOrDefault(predicate);
                return propertyForSearch != null;
            });
            property = propertyForSearch;

            return property != null;
        }

        public bool FindProperty(string name, out XmlPropertyGroup propertyGroup, out XmlProperty property)
        {
            return FindProperty(p => p.Name == name, out propertyGroup, out property);
        }

        #endregion Properties

        #region Property Groups

        /// <summary>
        ///     The property group that contains &lt;ProjectGuid&gt;, &lt;OutputType&gt;, &lt;RootNamespace&gt;, etc.
        /// </summary>
        public XmlPropertyGroup DefaultPropertyGroup { get; private set; }

        public XmlPropertyGroup FindPropertyGroup(Func<XmlPropertyGroup, bool> predicate)
            => Xml.PropertyGroups.FirstOrDefault(predicate);

        public IEnumerable<XmlPropertyGroup> FindPropertyGroups(Func<XmlPropertyGroup, bool> predicate)
            => Xml.PropertyGroups.Where(predicate);

        public XmlPropertyGroup CreatePropertyGroupAfter(XmlElement afterMe)
            => CreateAndInsertXmlElement(afterMe, Xml.CreatePropertyGroupElement, Xml.InsertAfterChild);

        public XmlPropertyGroup CreatePropertyGroupBefore(XmlElement beforeMe)
            => CreateAndInsertXmlElement(beforeMe, Xml.CreatePropertyGroupElement, Xml.InsertBeforeChild);

        private void InitializePropertyGroups()
        {
            DefaultPropertyGroup = FindPropertyGroup(g => g.Properties.Any(p => p.Name == "ProjectGuid"));
        }

        #region Conditional

        /// <summary>
        ///     Parse and enumerate conditional property groups which contains condition named
        ///     <see cref="ConditionNames.Configuration" />.
        /// </summary>
        /// <param name="configurationValuePredicate">
        ///     Predicate with value of configurations that determines which property groups should be results.
        /// </param>
        /// <returns>
        ///     An enumerable collection that contains <see cref="KeyValuePair{TKey, TValue}" />s mapping from configuration value
        ///     to its corresponding property group pair.
        /// </returns>
        public IEnumerable<KeyValuePair<string, XmlPropertyGroup>>
            ParseConfigurationPropertyGroups(Func<string, bool> configurationValuePredicate)
        {
            return ParseConditionalPropertyGroups(c => c.ContainsKey(ConditionNames.Configuration))
                .Select(p => new KeyValuePair<string, XmlPropertyGroup>(p.Key[ConditionNames.Configuration], p.Value))
                .Where(p => configurationValuePredicate == null || configurationValuePredicate(p.Key));
        }

        /// <summary>
        ///     Enumerate conditional property groups that matching specified predicate, or enumerate all conditional property
        ///     groups if specified predicate is <see langword="null" />.
        /// </summary>
        public IEnumerable<XmlPropertyGroup> FindConditionalPropertyGroups(Func<string, bool> predicate)
        {
            return Xml.PropertyGroups
                .Where(g => !string.IsNullOrEmpty(g.Condition) && (predicate == null || predicate(g.Condition)));
        }

        public IEnumerable<KeyValuePair<Conditions, XmlPropertyGroup>>
            ParseConditionalPropertyGroups(Func<Conditions, bool> predicate)
        {
            foreach (var group in Xml.PropertyGroups)
            {
                var conditionText = group.Condition;
                if (string.IsNullOrEmpty(conditionText))
                    continue;

                var condition = new Conditions(ParseConditions(conditionText));
                if (predicate == null || predicate(condition))
                    yield return new KeyValuePair<Conditions, XmlPropertyGroup>(condition, group);
            }
        }

        #region Parse

        /// <summary>
        ///     Parse the specified condition text and get condition value with specified name.
        /// </summary>
        /// <param name="text">Condition text to parse.</param>
        /// <param name="name">Name of the condition.</param>
        /// <returns>
        ///     Value of the condition with specified <paramref name="name" />, or <see langword="null" /> if condition with
        ///     specified <paramref name="name" /> doesn't exist.
        /// </returns>
        public static string ParseCondition(string text, string name)
        {
            return ParseConditions(text).FirstOrDefault(c => c.Name == name).Value;
        }

        public static IEnumerable<Condition> ParseConditions(string text)
        {
            var match = conditionRegex.Match(text);
            if (!match.Success)
                throw new ArgumentException("Invalid format of condition text.", nameof(text));

            var names = match.Groups["Names"].Value.Split('|');
            var values = match.Groups["Values"].Value.Split('|');
            int count = names.Length;
            if (count != values.Length)
                throw new ArgumentException("Number of configuration and its value does not match.", nameof(text));

            for (int i = 0; i < count; ++i)
            {
                var nameMatch = configurationNameRegex.Match(names[i]);
                if (!nameMatch.Success)
                    throw new ArgumentException("Invalid format of configuration name.", nameof(text));
                yield return new Condition(nameMatch.Groups["Name"].Value, values[i]);
            }
        }

        private static readonly Regex conditionRegex = new Regex(@"\'(?<Names>.+)\'\s*==\s*\'(?<Values>.*)\'");
        private static readonly Regex configurationNameRegex = new Regex(@"\$\((?<Name>\w+)\)");

        #endregion Parse

        public static class ConditionNames
        {
            public const string Configuration = "Configuration";
            public const string Platform = "Platform";
        }

        public struct Condition : IEquatable<Condition>
        {
            public readonly string Name;
            public readonly string Value;

            public Condition(string name, string value)
            {
                Name = name;
                Value = value;
                hashCode = HashCode.Get(Name, Value);
            }

            public Condition(StringPair condition)
            {
                Name = condition.Key;
                Value = condition.Value;
                hashCode = HashCode.Get(Name, Value);
            }

            #region Equality

            public static bool operator ==(Condition l, Condition r)
                => EqualityComparer<Condition>.Default.Equals(l, r);

            public static bool operator !=(Condition l, Condition r)
                => !EqualityComparer<Condition>.Default.Equals(l, r);

            public bool Equals(Condition other) => string.Equals(Name, other.Name) && string.Equals(Value, other.Value);

            public override bool Equals(object obj) => obj is Condition condition && Equals(condition);

            public override int GetHashCode() => hashCode;

            [NonSerialized] private readonly int hashCode;

            #endregion Equality
        }

        public sealed class Conditions
            : IReadOnlyDictionary<string, string>, IReadOnlyList<Condition>, IEquatable<Conditions>
        {
            public int Count => list.Count;

            public Condition this[int index] => list[index];

            public string this[string key] => dict[key];

            public IEnumerable<string> Keys => dict.Keys;
            public IEnumerable<string> Values => dict.Values;

            public Conditions() : this((IEnumerable<Condition>)null) { }

            public Conditions(IEnumerable<StringPair> conditions)
                : this(conditions.Select(p => new Condition(p.Key, p.Value))) { }

            public Conditions(IEnumerable<Condition> conditions)
            {
                var dict = new Dictionary<string, string>();
                var list = new List<Condition>();
                if (conditions != null)
                {
                    foreach (var condition in conditions)
                    {
                        list.Add(condition);
                        dict.Add(condition.Name, condition.Value);
                    }
                }
                this.list = list;
                this.dict = dict;

                namesString = new Lazy<string>(AppendNames(new StringBuilder(), false).ToString);
                valuesString = new Lazy<string>(AppendValues(new StringBuilder(), false).ToString);
                str = new Lazy<string>(BuildString);
                hashCode = HashCode.Get(list);
            }

            public bool ContainsKey(string key) { return dict.ContainsKey(key); }

            public bool TryGetValue(string key, out string value) { return dict.TryGetValue(key, out value); }

            public IEnumerator<Condition> GetEnumerator() { return list.GetEnumerator(); }

            private readonly IReadOnlyDictionary<string, string> dict;
            private readonly IReadOnlyList<Condition> list;

            #region Strings

            public string NamesString => namesString.Value;
            public string ValuesString => valuesString.Value;

            public override string ToString() => str.Value;

            [NonSerialized] private readonly Lazy<string> namesString;
            [NonSerialized] private readonly Lazy<string> valuesString;
            [NonSerialized] private readonly Lazy<string> str;

            private string BuildString()
            {
                var builder = new StringBuilder();

                builder.Append(' ');

                AppendNames(builder, true);
                builder.Append(" == ");
                AppendValues(builder, true);

                builder.Append(' ');

                return builder.ToString();
            }

            private StringBuilder AppendNames(StringBuilder builder, bool quote)
            {
                if (quote)
                    builder.Append('\'');

                foreach (var condition in list)
                    builder.Append($@"$({condition.Name})").Append('|');
                builder.RemoveTail(1);

                if (quote)
                    builder.Append('\'');

                return builder;
            }

            private StringBuilder AppendValues(StringBuilder builder, bool quote)
            {
                if (quote)
                    builder.Append('\'');

                foreach (var condition in list)
                    builder.Append(condition.Value).Append('|');
                builder.RemoveTail(1);

                if (quote)
                    builder.Append('\'');

                return builder;
            }

            #endregion Strings

            #region Equality

            public static bool operator ==(Conditions l, Conditions r)
                => EqualityComparer<Conditions>.Default.Equals(l, r);

            public static bool operator !=(Conditions l, Conditions r)
                => !EqualityComparer<Conditions>.Default.Equals(l, r);

            public bool Equals(Conditions other) { return list.SequenceEqual(other.list); }

            public override bool Equals(object obj) { return obj is Conditions conditions && Equals(conditions); }

            public override int GetHashCode() => hashCode;

            [NonSerialized] private readonly int hashCode;

            #endregion Equality

            #region Explicit Implementations

            IEnumerator<StringPair> IEnumerable<StringPair>.GetEnumerator() { return dict.GetEnumerator(); }
            IEnumerator IEnumerable.GetEnumerator() { return list.GetEnumerator(); }

            #endregion Explicit Implementations
        }

        #endregion Conditional

        #endregion Property Groups

        #region Items

        public XmlItem FindItem(Func<XmlItem, bool> predicate) => Xml.Items.FirstOrDefault(predicate);

        public IEnumerable<XmlItem> FindItems(Func<XmlItem, bool> predicate) => Xml.Items.Where(predicate);

        #endregion Items

        #region Item Groups

        public XmlItemGroup DefaultReferenceGroup { get; private set; }

        public XmlItemGroup FindItemGroup(Func<XmlItemGroup, bool> predicate)
            => Xml.ItemGroups.FirstOrDefault(predicate);

        public IEnumerable<XmlItemGroup> FindItemGroups(Func<XmlItemGroup, bool> predicate)
            => Xml.ItemGroups.Where(predicate);

        public XmlItemGroup CreateItemGroupAfter(XmlElement afterMe)
            => CreateAndInsertXmlElement(afterMe, Xml.CreateItemGroupElement, Xml.InsertAfterChild);

        public XmlItemGroup CreateItemGroupBefore(XmlElement beforeMe)
            => CreateAndInsertXmlElement(beforeMe, Xml.CreateItemGroupElement, Xml.InsertBeforeChild);

        private void InitializeItemGroups()
        {
            DefaultReferenceGroup = FindItemGroup(g => g.Items.Any(i => i.Include == "System"));
        }

        #endregion Item Groups

        #region Imports

        public XmlImport MSBuildToolsImport;

        public XmlImport FindImport(string project) { return FindImport(i => i.Project == project); }

        public XmlImport FindImport(Func<XmlImport, bool> predicate) { return Xml.Imports.FirstOrDefault(predicate); }

        private void InitializeImports()
        {
            MSBuildToolsImport = FindImport(@"$(MSBuildToolsPath)\Microsoft.CSharp.targets");
        }

        #endregion Imports

        private T CreateAndInsertXmlElement<T>(XmlElement insertAnchor,
            Func<T> createMethod, Action<XmlElement, XmlElement> insertMethod)
            where T : XmlElement
        {
            Ensure.Argument.NotNull(insertAnchor, nameof(insertAnchor));
            if (insertAnchor.ContainingProject != Xml)
                throw new ArgumentException("Child of current project required.", nameof(insertAnchor));

            var group = createMethod();
            insertMethod(group, insertAnchor);
            return group;
        }

        #endregion Xml

        #region Persistency

        public void Save()
        {
            PrepareSave();
            MSBuildProject.Save();
        }

        public void Save(string path)
        {
            PrepareSave();
            MSBuildProject.Save(path);
        }

        private void PrepareSave() => MSBuildProject.ReevaluateIfNecessary();

        public static void SaveAll()
        {
            foreach (var inst in Instances)
                inst.Save();
        }

        #endregion Persistency

        #region Instances

        public static IReadOnlyCollection<VSProject> Instances => instances.Values;

        public static VSProject Clone(VSProject project, string newPath, bool overwrite)
        {
            Ensure.Argument.NotNull(project, nameof(project));
            Ensure.Argument.NotNullOrEmpty(newPath, nameof(newPath));

            if (overwrite)
            {
                Unload(newPath);
            }
            else
            {
                if (instances.ContainsKey(newPath))
                    throw new InvalidOperationException("Project at specified path already loaded.");
                if (File.Exists(newPath))
                    throw new InvalidOperationException("Project at specified path already existed.");
            }

            // Create new project instance.
            var newXml = project.Xml.DeepClone();
            newXml.FullPath = newPath;
            var clonedProject = new VSProject(new Project(newXml));

            // Convert path of sources.
            var oldProjDir = project.DirectoryPath + Path.DirectorySeparatorChar;
            var newProjDir = clonedProject.DirectoryPath + Path.DirectorySeparatorChar;
            var items = clonedProject.FindItems(i => i.ElementName == "Compile");
            foreach (var item in items)
            {
                var fullPath = Path.GetFullPath(Path.Combine(oldProjDir, item.Include));
                item.Include = PathEx.MakeRelativePath(newProjDir, fullPath);

                var link = item.Metadata.FirstOrDefault(m => m.ElementName == "Link");
                if (link == null)
                    item.AddMetadata("Link", Path.GetFileName(item.Include));
            }

            return AddInstance(clonedProject);
        }

        /// <summary>
        ///     Loads a project file at specifiled path anyway. If the project at specified path is already loaded, reloaed it.
        /// </summary>
        /// <param name="path">Path of the project file.</param>
        /// <returns>A <see cref="VSProject" /> instance that represents the loaded project.</returns>
        public static VSProject Load(string path)
        {
            if (instances.TryGetValue(path, out var instance))
                UnloadImpl(path, instance);
            return LoadImpl(path);
        }

        public static VSProject Get(string path)
        {
            return instances.TryGetValue(path, out var instance) ? instance : null;
        }

        public static VSProject GetOrLoad(string path)
        {
            return instances.TryGetValue(path, out var instance) ? instance : LoadImpl(path);
        }

        public static bool Unload(string path)
        {
            if (!instances.TryGetValue(path, out var instance))
                return false;
            UnloadImpl(path, instance);
            return true;
        }

        public static void UnloadAll()
        {
            foreach (var kvp in new Dictionary<string, VSProject>(instances))
                UnloadImpl(kvp.Key, kvp.Value);
        }

        private static readonly Dictionary<string, VSProject> instances = new Dictionary<string, VSProject>();

        private static VSProject LoadImpl(string path) => AddInstance(new VSProject(path));

        private static VSProject LoadImpl(ProjectRootElement xml) => AddInstance(new VSProject(new Project(xml)));

        private static VSProject AddInstance(VSProject instance)
        {
            instances.Add(instance.FilePath, instance);
            return instance;
        }

        private static void UnloadImpl(string path, VSProject instance)
        {
            instances.Remove(path);
            instance.Dispose();
        }

        #endregion Instances
    }
}