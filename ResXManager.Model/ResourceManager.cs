﻿namespace tomenglertde.ResXManager.Model
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.ComponentModel.Composition;
    using System.Diagnostics.CodeAnalysis;
    using System.Diagnostics.Contracts;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Windows;
    using System.Windows.Data;

    using tomenglertde.ResXManager.Infrastructure;
    using tomenglertde.ResXManager.Model.Properties;

    using TomsToolbox.Core;
    using TomsToolbox.Desktop;
    using TomsToolbox.ObservableCollections;
    using TomsToolbox.Wpf;

    /// <summary>
    /// Represents all resources found in a folder and its's sub folders.
    /// </summary>
    [Export]
    public class ResourceManager : ObservableObject
    {
        private static readonly string[] _sortedCultureNames = GetSortedCultureNames();
        private static readonly CultureInfo[] _specificCultures = GetSpecificCultures();

        private readonly Configuration _configuration;
        private readonly CodeReferenceTracker _codeReferenceTracker;
        private readonly ITracer _tracer;

        private readonly ObservableCollection<ResourceEntity> _selectedEntities = new ObservableCollection<ResourceEntity>();
        private readonly IObservableCollection<ResourceTableEntry> _resourceTableEntries;
        private readonly ObservableCollection<ResourceTableEntry> _selectedTableEntries = new ObservableCollection<ResourceTableEntry>();
        private readonly ObservableCollection<ResourceEntity> _resourceEntities = new ObservableCollection<ResourceEntity>();

        private ObservableCollection<CultureKey> _cultureKeys = new ObservableCollection<CultureKey>();

        private string _snapshot;

        public event EventHandler<LanguageEventArgs> LanguageSaved;
        public event EventHandler<ResourceBeginEditingEventArgs> BeginEditing;
        public event EventHandler<EventArgs> Loaded;
        public event EventHandler<EventArgs> ReloadRequested;

        [ImportingConstructor]
        private ResourceManager(Configuration configuration, CodeReferenceTracker codeReferenceTracker, ITracer tracer)
        {
            Contract.Requires(configuration != null);
            Contract.Requires(codeReferenceTracker != null);
            Contract.Requires(tracer != null);

            _configuration = configuration;
            _codeReferenceTracker = codeReferenceTracker;
            _tracer = tracer;
            _resourceTableEntries = _selectedEntities.ObservableSelectMany(entity => entity.Entries);
        }

        /// <summary>
        /// Loads all resources from the specified project files.
        /// </summary>
        /// <param name="allSourceFiles">All resource x files.</param>
        public void Load<T>(IList<T> allSourceFiles)
            where T : ProjectFile
        {
            Contract.Requires(allSourceFiles != null);

            _codeReferenceTracker.StopFind();

            var resourceFilesByDirectory = allSourceFiles
                .Where(file => file.IsResourceFile())
                .GroupBy(file => file.GetBaseDirectory());

            InternalLoad(resourceFilesByDirectory);
        }

        /// <summary>
        /// Saves all modified resource files.
        /// </summary>
        /// <exception cref="IOException"></exception>
        /// <exception cref="UnauthorizedAccessException"></exception>
        public void Save()
        {
            var changedResourceLanguages = _resourceEntities
                .SelectMany(entity => entity.Languages)
                .Where(lang => lang.HasChanges);

            foreach (var resourceLanguage in changedResourceLanguages)
            {
                Contract.Assume(resourceLanguage != null);
                resourceLanguage.Save();
            }
        }

        public ICollection<ResourceEntity> ResourceEntities
        {
            get
            {
                Contract.Ensures(Contract.Result<IEnumerable<ResourceEntity>>() != null);

                return _resourceEntities;
            }
        }

        public ICollection<ResourceTableEntry> ResourceTableEntries
        {
            get
            {
                Contract.Ensures(Contract.Result<IEnumerable<ResourceTableEntry>>() != null);

                return _resourceTableEntries;
            }
        }

        public ICollection<CultureKey> CultureKeys
        {
            get
            {
                Contract.Ensures(Contract.Result<IEnumerable<CultureKey>>() != null);

                return _cultureKeys;
            }
        }

        public ObservableCollection<ResourceEntity> SelectedEntities
        {
            get
            {
                Contract.Ensures(Contract.Result<IList<ResourceEntity>>() != null);

                return _selectedEntities;
            }
        }

        public IList<ResourceTableEntry> SelectedTableEntries
        {
            get
            {
                Contract.Ensures(Contract.Result<IList<ResourceTableEntry>>() != null);

                return _selectedTableEntries;
            }
        }

        public static IEnumerable<CultureInfo> SpecificCultures
        {
            get
            {
                Contract.Ensures(Contract.Result<IEnumerable<CultureInfo>>() != null);

                return _specificCultures;
            }
        }

        public Configuration Configuration
        {
            get
            {
                Contract.Ensures(Contract.Result<Configuration>() != null);

                return _configuration;
            }
        }

        public void AddNewKey(ResourceEntity entity, string key)
        {
            Contract.Requires(entity != null);
            Contract.Requires(!string.IsNullOrEmpty(key));

            if (!entity.CanEdit(null))
                return;

            var entry = entity.Add(key);
            if (entry == null)
                return;

            if (!string.IsNullOrEmpty(_snapshot))
                _resourceEntities.LoadSnapshot(_snapshot);

            _selectedTableEntries.Clear();
            _selectedTableEntries.Add(entry);
        }

        public void LanguageAdded(CultureInfo culture)
        {
            if (!_configuration.AutoCreateNewLanguageFiles)
                return;

            foreach (var resourceEntity in _resourceEntities)
            {
                Contract.Assume(resourceEntity != null);

                if (!CanEdit(resourceEntity, culture))
                    break;
            }
        }

        public void Reload()
        {
            OnReloadRequested();
        }

        public bool CanEdit(ResourceEntity resourceEntity, CultureInfo culture)
        {
            Contract.Requires(resourceEntity != null);

            var eventHandler = BeginEditing;

            if (eventHandler == null)
                return true;

            var args = new ResourceBeginEditingEventArgs(resourceEntity, culture);

            eventHandler(this, args);

            return !args.Cancel;
        }

        private void OnLoaded()
        {
            var handler = Loaded;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private void OnLanguageSaved(LanguageEventArgs e)
        {
            var handler = LanguageSaved;
            if (handler != null)
                handler(this, e);
        }

        private void OnReloadRequested()
        {
            var handler = ReloadRequested;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private void InternalLoad(IEnumerable<IGrouping<string, ProjectFile>> resourceFilesByDirectory)
        {
            Contract.Requires(resourceFilesByDirectory != null);

            var entities = GetResourceEntities(resourceFilesByDirectory)
                .OrderBy(e => e.ProjectName)
                .ThenBy(e => e.BaseName);

            var isReloading = _resourceEntities.Any();

            var selectedEntities = _selectedEntities.ToArray();
            var selectedTableEntries = _selectedTableEntries.ToArray();

            _resourceEntities.Clear();
            _resourceEntities.AddRange(entities);

            if (!string.IsNullOrEmpty(_snapshot))
                _resourceEntities.LoadSnapshot(_snapshot);

            var cultureKeys = _resourceEntities.SelectMany(entity => entity.Languages).Distinct().Select(lang => lang.CultureKey);
            _cultureKeys = new ObservableCollection<CultureKey>(cultureKeys);

            _selectedEntities.Clear();
            _selectedEntities.AddRange(isReloading ? _resourceEntities.Where(selectedEntities.Contains) : _resourceEntities);

            _selectedTableEntries.Clear();
            _selectedTableEntries.AddRange(_resourceTableEntries.Where(entry => selectedTableEntries.Contains(entry, ResourceTableEntry.EqualityComparer)));

            OnPropertyChanged(() => CultureKeys);
            OnLoaded();
        }

        private IEnumerable<ResourceEntity> GetResourceEntities(IEnumerable<IGrouping<string, ProjectFile>> fileNamesByDirectory)
        {
            Contract.Requires(fileNamesByDirectory != null);

            foreach (var directory in fileNamesByDirectory)
            {
                Contract.Assume(directory != null);

                var directoryName = directory.Key;
                Contract.Assume(!string.IsNullOrEmpty(directoryName));

                var filesByBaseName = directory.GroupBy(file => file.GetBaseName());

                foreach (var files in filesByBaseName)
                {
                    if ((files == null) || !files.Any())
                        continue;

                    var baseName = files.Key;
                    Contract.Assume(!string.IsNullOrEmpty(baseName));

                    var filesByProject = files.GroupBy(file => file.ProjectName);

                    foreach (var projectFiles in filesByProject)
                    {
                        if (projectFiles == null)
                            continue;

                        var projectName = projectFiles.Key;

                        if (string.IsNullOrEmpty(projectName))
                            continue;

                        var resourceEntity = new ResourceEntity(this, projectName, baseName, directoryName, files.ToArray());

                        resourceEntity.LanguageChanging += ResourceEntity_LanguageChanging;
                        resourceEntity.LanguageChanged += ResourceEntity_LanguageChanged;
                        resourceEntity.LanguageAdded += ResourceEntity_LanguageAdded;

                        yield return resourceEntity;
                    }
                }
            }
        }

        private void ResourceEntity_LanguageAdded(object sender, LanguageChangedEventArgs e)
        {
            var cultureKey = e.Language.CultureKey;

            if (!_cultureKeys.Contains(cultureKey))
            {
                _cultureKeys.Add(cultureKey);
            }
        }

        private void ResourceEntity_LanguageChanging(object sender, LanguageChangingEventArgs e)
        {
            if (!CanEdit(e.Entity, e.Culture))
            {
                e.Cancel = true;
            }
        }

        private void ResourceEntity_LanguageChanged(object sender, LanguageChangedEventArgs e)
        {
            // Defer save to avoid repeated file access
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (!e.Language.HasChanges)
                        return;

                    e.Language.Save();

                    OnLanguageSaved(e);
                }
                catch (Exception ex)
                {
                    _tracer.TraceError(ex.ToString());
                    MessageBox.Show(ex.Message, Resources.Title);
                }
            });
        }

        public static bool IsValidLanguageName(string languageName)
        {
            return Array.BinarySearch(_sortedCultureNames, languageName, StringComparer.OrdinalIgnoreCase) >= 0;
        }

        private static string[] GetSortedCultureNames()
        {
            var allCultures = CultureInfo.GetCultures(CultureTypes.AllCultures);

            var cultureNames = allCultures
                .SelectMany(culture => new[] { culture.IetfLanguageTag, culture.Name })
                .Distinct()
                .ToArray();

            Array.Sort(cultureNames, StringComparer.OrdinalIgnoreCase);

            return cultureNames;
        }

        private static CultureInfo[] GetSpecificCultures()
        {
            var specificCultures = CultureInfo.GetCultures(CultureTypes.AllCultures)
                .Where(c => c.GetAncestors().Any())
                .OrderBy(c => c.DisplayName)
                .ToArray();

            return specificCultures;
        }

        public void LoadSnapshot(string value)
        {
            ResourceEntities.LoadSnapshot(value);

            _snapshot = value;
        }

        public string CreateSnapshot()
        {
            Contract.Ensures(Contract.Result<string>() != null);

            return _snapshot = ResourceEntities.CreateSnapshot();
        }

        [ContractInvariantMethod]
        [SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic", Justification = "Required for code contracts.")]
        private void ObjectInvariant()
        {
            Contract.Invariant(_resourceEntities != null);
            Contract.Invariant(_selectedEntities != null);
            Contract.Invariant(_selectedTableEntries != null);
            Contract.Invariant(_resourceTableEntries != null);
            Contract.Invariant(_configuration != null);
            Contract.Invariant(_cultureKeys != null);
        }
    }
}
