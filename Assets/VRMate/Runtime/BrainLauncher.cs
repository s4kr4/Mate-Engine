using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VRMate
{
    /// <summary>
    /// Starts the brain process described by persistentDataPath/VRMate/brain.json when the built app
    /// starts, and keeps its standard input open while the app runs: the brain treats the end of its input
    /// as the signal to stop, so the input is closed when the app quits (or dies, which closes it too).
    /// Nothing is written to the input, the brain is not waited for or killed, and it is not restarted.
    /// Without brain.json nothing happens; in the editor nothing happens either, so the brain started by
    /// hand keeps working there. The brain's standard output and error go to brain.log next to brain.json,
    /// overwritten on every start and limited in size by BrainLog. The process events arrive on other
    /// threads, so everything they touch is guarded by one lock and is a no-op once the log is closed.
    /// </summary>
    [DisallowMultipleComponent]
    public class BrainLauncher : MonoBehaviour
    {
        private static BrainLauncher instance;

        private readonly object gate = new object();

        // Guarded by gate.
        private Process process;
        private StreamWriter logWriter;
        private BrainLog brainLog;
        private bool quitting;
        private bool destroyed;
        private bool outputEnded;
        private bool errorEnded;
        private string exitLine;

        private void Awake()
        {
            // A second copy would start a second brain.
            if (instance != null && instance != this)
            {
                Destroy(this);
                return;
            }

            instance = this;
        }

        private void Start()
        {
            if (instance != this || Application.isEditor)
            {
                return;
            }

            var folder = Path.Combine(Application.persistentDataPath, "VRMate");
            BrainLaunchSettings settings;
            string problem;
            switch (BrainLaunchSettings.Load(Path.Combine(folder, "brain.json"), out settings, out problem))
            {
                case BrainLaunchLoad.Missing:
                    return;
                case BrainLaunchLoad.Invalid:
                    Debug.LogWarning("[VRMate] the brain is not started: " + problem);
                    return;
                default:
                    Launch(settings, Path.Combine(folder, "brain.log"));
                    return;
            }
        }

        private void OnApplicationQuit()
        {
            Process running;
            lock (gate)
            {
                quitting = true;
                running = process;
            }

            CloseInput(running);
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }

            Process running;
            lock (gate)
            {
                destroyed = true;
                running = process;
                CloseLog();
            }

            // The process object is not disposed: quitting frees it with the app, and disposing it while the
            // reader threads are still running would give them another way to fail.

            // Also here in case the component goes away without the app quitting: the input is open only
            // while this component can still watch the brain.
            CloseInput(running);
        }

        private void Launch(BrainLaunchSettings settings, string logPath)
        {
            StreamWriter writer;
            try
            {
                // Readable while it is being written, so the log can be followed as the brain runs.
                writer = new StreamWriter(
                    new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                    BrainLog.Utf8);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VRMate] the brain is not started: brain.log could not be opened: " + Describe(e));
                return;
            }

            lock (gate)
            {
                logWriter = writer;
                brainLog = new BrainLog();
            }

            Process started = null;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = settings.Command,
                    Arguments = settings.Arguments,
                    WorkingDirectory = settings.WorkingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = BrainLog.Utf8,
                    StandardErrorEncoding = BrainLog.Utf8,

                    // Without this, Mono writes the input encoding's BOM (the console's, which can be UTF-8
                    // with a BOM) into the brain's input as soon as it starts. A preamble-free encoding keeps
                    // the input empty.
                    StandardInputEncoding = BrainLog.Utf8,
                };
                startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

                started = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                started.OutputDataReceived += OnOutput;
                started.ErrorDataReceived += OnError;
                started.Exited += OnExited;
                lock (gate)
                {
                    process = started;
                }

                // Mono duplicates the parent's end of the input pipe as non-inheritable and closes the
                // inheritable original, so closing the input reaches the brain as the end of its input.
                started.Start();
                started.BeginOutputReadLine();
                started.BeginErrorReadLine();
            }
            catch (Exception e)
            {
                var message = "the brain could not be started: " + Describe(e);
                Debug.LogWarning("[VRMate] " + message);
                lock (gate)
                {
                    WriteLine(message);
                    process = null;
                    CloseLog();
                }

                CloseInput(started);
            }
        }

        private void OnOutput(object sender, DataReceivedEventArgs e)
        {
            OnLine(e.Data, false);
        }

        private void OnError(object sender, DataReceivedEventArgs e)
        {
            OnLine(e.Data, true);
        }

        // Runs on a reader thread; an exception here would be unhandled on that thread.
        private void OnLine(string line, bool fromError)
        {
            try
            {
                lock (gate)
                {
                    if (line != null)
                    {
                        WriteLine(line);
                        return;
                    }

                    if (fromError)
                    {
                        errorEnded = true;
                    }
                    else
                    {
                        outputEnded = true;
                    }

                    WriteExitLineWhenOutputEnded();
                }
            }
            catch (Exception)
            {
                // Nothing is worth bringing the app down for; WriteLine already reports write errors.
            }
        }

        // Runs on a thread-pool thread, possibly before the last lines of output have been read.
        private void OnExited(object sender, EventArgs e)
        {
            try
            {
                string code;
                try
                {
                    code = ((Process)sender).ExitCode.ToString(CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    code = "unknown";
                }

                var message = "the brain exited with code " + code + "; it is not restarted";
                bool warn;
                lock (gate)
                {
                    // The brain is expected to stop once the input is closed on quit or on destroy. A brain
                    // that failed to start was already reported, and is no longer the current process. The
                    // warning does not depend on the log, which may have been given up after a write error.
                    warn = !quitting && !destroyed && ReferenceEquals(sender, process);
                    if (warn && logWriter != null)
                    {
                        exitLine = message;
                        WriteExitLineWhenOutputEnded();
                    }
                }

                if (warn)
                {
                    Debug.LogWarning("[VRMate] " + message);
                }
            }
            catch (Exception)
            {
                // Same as OnLine.
            }
        }

        // Called with gate held. The exit line goes after the brain's own last lines.
        private void WriteExitLineWhenOutputEnded()
        {
            if (exitLine == null || !outputEnded || !errorEnded)
            {
                return;
            }

            WriteLine(exitLine);
            exitLine = null;
        }

        // Called with gate held; a no-op once the log is closed.
        private void WriteLine(string line)
        {
            if (logWriter == null)
            {
                return;
            }

            var text = brainLog.Accept(line);
            if (text == null)
            {
                return;
            }

            try
            {
                logWriter.Write(text);
                logWriter.Flush();
            }
            catch (Exception e)
            {
                // A full disk or a removed drive: stop logging rather than fail on every line.
                Debug.LogWarning("[VRMate] brain.log could not be written; later output is dropped: " + Describe(e));
                CloseLog();
            }
        }

        // Called with gate held.
        private void CloseLog()
        {
            var writer = logWriter;
            logWriter = null;
            if (writer == null)
            {
                return;
            }

            try
            {
                writer.Dispose();
            }
            catch (Exception)
            {
                // The log is given up either way.
            }
        }

        private static void CloseInput(Process running)
        {
            if (running == null)
            {
                return;
            }

            try
            {
                // Not waited for and not killed: the brain stops by itself on the end of its input.
                running.StandardInput.Dispose();
            }
            catch (Exception)
            {
                // Not started, already closed, or the brain has gone: there is no input left to close.
            }
        }

        /// <summary>The exception type and message on one line.</summary>
        private static string Describe(Exception e)
        {
            var win32 = e as Win32Exception;
            var text = e.GetType().Name + (win32 != null ? " " + win32.NativeErrorCode : string.Empty) + ": " + e.Message;
            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                builder.Append(IsLineBreak(c) ? ' ' : c);
            }

            return builder.ToString();
        }

        // NEL, LINE SEPARATOR and PARAGRAPH SEPARATOR break lines too, and would split the one-line warning.
        private static bool IsLineBreak(char c)
        {
            return c == '\r' || c == '\n' || c == (char)0x0085 || c == (char)0x2028 || c == (char)0x2029;
        }
    }
}
