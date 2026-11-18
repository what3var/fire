using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using fire.Projects;

namespace fire.Editor
{
    // The folders of a project (docs/tools/projects.md, "Folders"): Exclude from Project, Include in Project and Remove Exclusion of the explorer's context menu, for every selected folder;
    // with "Sync .gitignore" (the project's Build page, on by default) the .gitignore follows (GitignoreSync).
    public partial class MainWindow
    {
        private void OnFolderCommand(string command, ExplorerNode node)
        {
            Func<FolderInfo, bool> applies = command switch
            {
                "folder-exclude" => f => f.State != FolderState.Excluded,
                "folder-include" => f => f.State == FolderState.Undecided || f.IsOwnExclusion,
                "folder-unexclude" => f => f.IsOwnExclusion,
                _ => _ => false,
            };
            var folders = _solutionPanel.SelectedFolders(node, applies).Where(n => n.Folder != null && applies(n.Folder)).ToList();
            if (folders.Count == 0) return;
            var notes = new List<string>();
            int changed = 0;
            foreach (var group in folders.GroupBy(n => n.Project!))
            {
                var project = group.Key;
                bool sync = project.Project.SyncsGitignore;
                try
                {
                    foreach (var n in group)
                    {
                        string rel = ProjectFiles.Relative(project.Directory, n.Path!);
                        switch (command)
                        {
                            case "folder-exclude":
                                if (ProjectFolderEdits.Exclude(project.Project, rel)) changed++;
                                if (sync)
                                {
                                    var (file, line, added) = GitignoreSync.Add(project.Project, n.Path!);
                                    if (added) notes.Add($"'{line}' added to {file}");
                                }
                                break;
                            case "folder-include":
                            case "folder-unexclude":
                                bool was = n.Folder!.IsOwnExclusion;
                                if (command == "folder-include" ? ProjectFolderEdits.Include(project.Project, rel) : ProjectFolderEdits.RemoveExclusion(project.Project, rel)) changed++;
                                if (sync && was)
                                {
                                    var (file, foreign) = GitignoreSync.Remove(project.Project, n.Path!);
                                    if (file != null) notes.Add($"line for '{rel}' taken out of {file}");
                                    else if (foreign != null) notes.Add($"'{foreign}' stays in the .gitignore (spark did not write it): the folder is still ignored");
                                }
                                break;
                        }
                    }
                    _workspace.SaveProject(project);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectException)
                {
                    UpdateStatus($"{project.Name}: {ex.Message}");
                    return;
                }
            }
            string what = folders.Count == 1 ? $"'{ProjectFiles.Relative(folders[0].Project!.Directory, folders[0].Path!)}'" : $"{folders.Count} folders";
            string done = command switch
            {
                "folder-exclude" => $"Excluded {what} from the project (Show All Files shows it)",
                "folder-include" => $"Included {what} in the project",
                _ => $"Removed the exclusion of {what}",
            };
            UpdateStatus(done + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "") + (changed == 0 ? " (nothing changed)" : "") + ".");
        }
    }
}
