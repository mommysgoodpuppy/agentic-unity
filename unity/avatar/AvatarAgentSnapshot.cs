// Commits a snapshot of the project's Unity text data after every save, so a crash can't
// cost more than unsaved work. The snapshots go to a separate repository,
// ~/UnitySnapshots/<project>.git (made by tools/init_snapshots.py), whose work tree is this project and whose
// info/exclude allowlists text assets only. The project's own Git repository is never
// touched. Nothing happens if that snapshot repository doesn't exist.
//
// git runs in the background; the editor doesn't wait for it. Output of the last run goes
// to Library/VRUnityAgent/Snapshot/last.log.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class AvatarAgentSnapshot : AssetModificationProcessor
{
    static string Project => Path.GetDirectoryName(Application.dataPath);
    static string GitDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "UnitySnapshots", Path.GetFileName(Project) + ".git");
    static string Work => Path.Combine(Project, "Library/VRUnityAgent/Snapshot");

    static volatile bool running, pending, retried;
    static string[] lastSaved = new string[0];

    static string[] OnWillSaveAssets(string[] paths)
    {
        if (Directory.Exists(GitDir) && paths.Length > 0)
        {
            lastSaved = paths;
            // After the save has been written.
            EditorApplication.delayCall += Request;
        }
        return paths;
    }

    [InitializeOnLoadMethod]
    static void Init() => EditorApplication.update += () =>
    {
        // A save arrived while git was busy: snapshot again once it's done.
        if (pending && !running) { pending = false; Request(); }
    };

    static void Request()
    {
        if (running) { pending = true; return; }
        running = true;
        try
        {
            Directory.CreateDirectory(Work);
            var shown = lastSaved.Take(5).ToArray();
            string message = "Unity save: " + string.Join(", ", shown.Select(Path.GetFileName))
                + (lastSaved.Length > shown.Length ? $" (+{lastSaved.Length - shown.Length} more)" : "")
                + "\n\n" + string.Join("\n", lastSaved) + "\n";
            string messageFile = Path.Combine(Work, "message.txt");
            File.WriteAllText(messageFile, message);
            var start = new ProcessStartInfo("/bin/sh")
            {
                // Commit only when the allowlisted files actually changed.
                Arguments = "-c \"git add -A && (git diff --cached --quiet || git commit -q -F \\\"$SNAPSHOT_MESSAGE\\\") >\\\"$SNAPSHOT_LOG\\\" 2>&1\"",
                WorkingDirectory = Project,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.EnvironmentVariables["GIT_DIR"] = GitDir;
            start.EnvironmentVariables["GIT_WORK_TREE"] = Project;
            start.EnvironmentVariables["SNAPSHOT_MESSAGE"] = messageFile;
            start.EnvironmentVariables["SNAPSHOT_LOG"] = Path.Combine(Work, "last.log");
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.Exited += (_, __) =>
            {
                int code = process.ExitCode;
                process.Dispose();
                // A run interrupted by a domain reload can leave index.lock briefly; retry once.
                if (code != 0 && !retried) { retried = true; pending = true; }
                else retried = false;
                running = false;
            };
            process.Start();
        }
        catch (Exception e)
        {
            running = false;
            UnityEngine.Debug.LogWarning("[Snapshot] " + e.Message);
        }
    }
}
