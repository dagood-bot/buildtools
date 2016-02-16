using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json.Linq;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Microsoft.DotNet.Build.Tasks.RestoreValidation
{
    public class ValidateProjectDependencyVersions : VisitProjectDependencies
    {
        private delegate void LogAction(string format, params object[] args);

        private class ValidationPattern
        {
            private Regex _idPattern;
            private string _expectedVersion;
            private string _expectedPrerelease;
            private TaskLoggingHelper _log;

            public ValidationPattern(ITaskItem item, TaskLoggingHelper log)
            {
                _idPattern = new Regex(item.ItemSpec);
                _expectedVersion = item.GetMetadata("ExpectedVersion");
                _expectedPrerelease = item.GetMetadata("ExpectedPrerelease");
                _log = log;

                if (string.IsNullOrWhiteSpace(_expectedVersion))
                {
                    if (string.IsNullOrWhiteSpace(_expectedPrerelease))
                    {
                        _log.LogError(
                            "Can't find ExpectedVersion or ExpectedPrerelease metadata on item {0}",
                            item.ItemSpec);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(_expectedPrerelease))
                {
                    _log.LogError(
                        "Both ExpectedVersion and ExpectedPrerelease metadata found on item {0}, but only one permitted",
                        item.ItemSpec);
                }
            }

            public bool VisitPackage(
                JProperty package,
                string packageId,
                string version,
                string dependencyMessage,
                bool updateInvalidDependencies)
            {
                bool updatedPackage = false;

                var dependencyVersionRange = VersionRange.Parse(version);
                NuGetVersion dependencyVersion = dependencyVersionRange.MinVersion;

                LogAction logAction;
                string logPreamble;

                if (updateInvalidDependencies)
                {
                    logAction = _log.LogWarning;
                    logPreamble = "Fixing invalid dependency: ";
                }
                else
                {
                    logAction = _log.LogError;
                    logPreamble = "Dependency validation error: ";
                }
                logPreamble += "for " + dependencyMessage;

                if (_idPattern.IsMatch(packageId))
                {
                    if (!string.IsNullOrWhiteSpace(_expectedVersion) && _expectedVersion != version)
                    {
                        if (updateInvalidDependencies)
                        {
                            package.Value = _expectedVersion;
                            updatedPackage = true;
                        }
                        logAction(
                            "{0} package version is '{1}' but expected '{2}' for packages matching '{3}'",
                            logPreamble,
                            version,
                            _expectedVersion,
                            _idPattern);
                    }
                    if (!string.IsNullOrWhiteSpace(_expectedPrerelease) &&
                        dependencyVersion.IsPrerelease &&
                        _expectedPrerelease != dependencyVersion.Release)
                    {
                        if (updateInvalidDependencies)
                        {
                            package.Value = new NuGetVersion(
                                dependencyVersion.Major,
                                dependencyVersion.Minor,
                                dependencyVersion.Patch,
                                _expectedPrerelease,
                                dependencyVersion.Metadata).ToNormalizedString();
                            updatedPackage = true;
                        }
                        logAction(
                            "{0} package prerelease is '{1}', but expected '{2}' for packages matching '{3}'",
                            logPreamble,
                            dependencyVersion.Release,
                            _expectedPrerelease,
                            _idPattern);
                    }
                }
                return updatedPackage;
            }
        }

        private class UniquePackageEntry
        {
            private List<DependencySource> _dependencies = new List<DependencySource>();

            public IEnumerable<DependencySource> Dependencies { get { return _dependencies; } }

            public bool Conflict { get; private set; }

            public void AddSighting(string id, string projectFilePath)
            {
                Conflict |= _dependencies.Count > 0 && _dependencies[0].Id != id;
                _dependencies.Add(new DependencySource { Id = id, ProjectFilePath = projectFilePath });
            }

            public string GetPreferredId()
            {
                return Dependencies.OrderByDescending(d => d.Id).First().Id;
            }
        }

        private class DependencySource
        {
            public string Id { get; set; }
            public string ProjectFilePath { get; set; }
        }

        /// <summary>
        /// Prohibits floating dependencies, aka "*" dependencies. Defaults to false, allowing them.
        /// </summary>
        public bool ProhibitFloatingDependencies { get; set; }

        /// <summary>
        /// Enforces that all dependencies on the same package (case-insensitive match) are
        /// precisely the same, case-sensitively. Works around https://github.com/NuGet/Home/issues/2102
        /// </summary>
        public bool ProhibitCaseMismatch { get; set; }

        /// <summary>
        /// A set of patterns to enforce for package dependencies. If not specified, all
        /// versions are permitted for any package.
        /// </summary>
        public ITaskItem[] ValidationPatterns { get; set; }

        /// <summary>
        /// If true, when an invalid dependency is encountered it is changed to the valid version.
        /// </summary>
        public bool UpdateInvalidDependencies { get; set; }

        /// <summary>
        /// Mapping from all packages seen so far in lowercase to their exact IDs.
        /// </summary>
        private Dictionary<string, UniquePackageEntry> _uniquePackages = new Dictionary<string, UniquePackageEntry>();

        public override bool VisitPackage(JProperty package, string projectJsonPath)
        {
            var patterns = Enumerable.Empty<ValidationPattern>();

            if (ValidationPatterns != null)
            {
                patterns = ValidationPatterns
                    .Select(item => new ValidationPattern(item, Log))
                    .ToArray();
            }

            string id = package.Name;
            string version = package.Value.ToObject<string>();

            string dependencyMessage = string.Format(
                "{0} {1} in {2}",
                id,
                version,
                projectJsonPath);

            bool packageUpdated = false;

            foreach (var pattern in patterns)
            {
                packageUpdated |= pattern.VisitPackage(
                    package,
                    id,
                    version,
                    dependencyMessage,
                    UpdateInvalidDependencies);
            }

            if (!packageUpdated && ProhibitFloatingDependencies && version.Contains('*'))
            {
                // A * dependency was found but it hasn't been fixed. It might not have been fixed
                // because UpdateInvalidDependencies = false or because a pattern didn't match it:
                // either way this is an error.
                Log.LogError("Floating dependency detected: {0}", dependencyMessage);
            }

            if (ProhibitCaseMismatch)
            {
                string lowercaseId = id.ToLowerInvariant();
                UniquePackageEntry entry;
                if (!_uniquePackages.TryGetValue(lowercaseId, out entry))
                {
                    entry = _uniquePackages[lowercaseId] = new UniquePackageEntry();
                }
                entry.AddSighting(id, projectJsonPath);
            }

            return packageUpdated;
        }

        public override void PostVisitValidate()
        {
            if (ProhibitCaseMismatch)
            {
                foreach (var uniquePair in _uniquePackages.Where(p => p.Value.Conflict))
                {
                    string preferredId = uniquePair.Value.GetPreferredId();

                    if (!UpdateInvalidDependencies)
                    {
                        Log.LogMessage(
                            "Different capitalizations of {0}. UpdateInvalidDependencies would change to {1}.",
                            uniquePair.Key,
                            preferredId);
                    }

                    foreach (var dependency in uniquePair.Value.Dependencies)
                    {
                        if (UpdateInvalidDependencies)
                        {
                            if (dependency.Id != preferredId)
                            {
                                string projectJsonContents = File.ReadAllText(dependency.ProjectFilePath);

                                File.SetAttributes(
                                    dependency.ProjectFilePath,
                                    (File.GetAttributes(dependency.ProjectFilePath) | FileAttributes.ReadOnly) ^ FileAttributes.ReadOnly);
                                File.WriteAllText(
                                    dependency.ProjectFilePath,
                                    projectJsonContents.Replace(dependency.Id, preferredId));

                                Log.LogWarning(
                                    "Changed {0} to {1} in {2}",
                                    dependency.Id,
                                    preferredId,
                                    dependency.ProjectFilePath);
                            }
                        }
                        else
                        {
                            Log.LogError(
                                "Capitalization difference: {0} referred to as {1} in {2}",
                                uniquePair.Key,
                                dependency.Id,
                                dependency.ProjectFilePath);
                        }
                    }
                }
            }
        }
    }
}
