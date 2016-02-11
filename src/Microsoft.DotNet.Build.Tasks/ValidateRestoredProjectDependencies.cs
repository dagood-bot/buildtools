using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json.Linq;
using NuGet.ProjectModel;
using NuGet.Versioning;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.DotNet.Build.Tasks.RestoreValidation
{
    public class ValidateRestoredProjectDependencies : Task
    {
        [Required]
        public ITaskItem[] ProjectLockJsons { get; set; }

        public override bool Execute()
        {
            foreach (var projectLockItem in ProjectLockJsons)
            {
                var format = new LockFileFormat();
                var lockfile = format.Read(projectLockItem.ItemSpec);
                ValidateLockFile(lockfile, projectLockItem.ItemSpec);
            }

            return !Log.HasLoggedErrors;
        }

        private void ValidateLockFile(LockFile lockfile, string lockFilePath)
        {
            var lockedDependencyVersions = lockfile.Libraries.ToLookup(lib => lib.Name, lib => lib.Version);

            bool differencesFound = false;
            foreach (var requestedFrameworkDependencies in lockfile.ProjectFileDependencyGroups)
            {
                foreach (string dependency in requestedFrameworkDependencies.Dependencies)
                {
                    string[] dependencyParts = dependency.Split(' ');
                    string requestedId = dependencyParts[0];
                    string requestedVersion = dependencyParts[2];
                    NuGetVersion requestedNuGetVersion = NuGetVersion.Parse(requestedVersion);

                    IEnumerable<NuGetVersion> restoredVersions = lockedDependencyVersions[requestedId];

                    // Check if the requested package was restored. Normalize using NuGet version parsing.
                    if (!restoredVersions.Contains(requestedNuGetVersion))
                    {
                        HandleNonExistentDependency(requestedId, requestedVersion, restoredVersions, lockFilePath);
                        differencesFound = true;
                    }
                }
                if (differencesFound)
                {
                    Log.LogWarning(
                        "Found different requested version vs. restored version for framework {0} in {1}",
                        requestedFrameworkDependencies.FrameworkName,
                        lockFilePath);
                }
            }
        }

        protected virtual void HandleNonExistentDependency(
            string name,
            string version,
            IEnumerable<NuGetVersion> libraryVersionsRestored,
            string lockFilePath)
        {
            Log.LogError(
                "Desired version {0} {1} not restored, found '{2}' for {3}",
                name,
                version,
                string.Join(", ", libraryVersionsRestored),
                lockFilePath);
        }
    }
}
