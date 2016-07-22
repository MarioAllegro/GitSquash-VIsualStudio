using GitSquash.VisualStudio.Properties;

namespace GitSquash.VisualStudio
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using LibGit2Sharp;

    using PropertyChanged;

    /// <summary>
    /// A wrapper that performs the git squash operations.
    /// </summary>
    [ImplementPropertyChanged]
    public class GitSquashWrapper : IGitSquashWrapper
    {
        private static StringBuilder output = new StringBuilder();

        private static StringBuilder error = new StringBuilder();

        private static IGitSquashOutputLogger outputLogger;

        private readonly string repoDirectory;

        /// <summary>
        /// Initializes a new instance of the <see cref="GitSquashWrapper" /> class.
        /// </summary>
        /// <param name="repoDirectory">The directory where the repository is located</param>
        /// <param name="logger">The output logger to output git transactions.</param>
        public GitSquashWrapper(string repoDirectory, IGitSquashOutputLogger logger)
        {
            this.repoDirectory = repoDirectory;
            outputLogger = logger;
        }

        /// <inheritdoc />
        public string GetCommitMessages(GitCommit startCommit)
        {
            if (startCommit == null)
            {
                return null;
            }

            var sb = new StringBuilder();
            using (Repository repository = this.GetRepository())
            {
                foreach (Commit commit in repository.Head.Commits.TakeWhile(x => x.Sha != startCommit.Sha))
                {
                    sb.AppendLine(commit.Message.Trim());
                }
            }

            return TrimEmptyLines(sb.ToString());
        }

        /// <inheritdoc />
        public GitBranch GetCurrentBranch()
        {
            using (Repository repository = this.GetRepository())
            {
                return new GitBranch(repository.Head.FriendlyName);
            }
        }

        /// <inheritdoc />
        public async Task<GitCommandResponse> PushForce(CancellationToken token)
        {
            using (Repository repository = this.GetRepository())
            {
                if (repository.Head.IsTracking == false)
                {
                    return new GitCommandResponse(false, "The branch has not been pushed before.");
                }

                return await this.RunGit("push -f", token);
            }
        }

        /// <inheritdoc />
        public IEnumerable<GitCommit> GetCommitsForBranch(GitBranch branch, int number = 25)
        {
            using (Repository repository = this.GetRepository())
            {
                Branch internalBranch = repository.Branches.FirstOrDefault(x => x.FriendlyName == branch.FriendlyName);

                if (internalBranch == null)
                {
                    return Enumerable.Empty<GitCommit>();
                }

                return internalBranch.Commits.Take(number).Select(x => new GitCommit(x.Sha, x.MessageShort)).ToList();
            }
        }

        /// <inheritdoc />
        public bool IsRebaseHappening()
        {
            bool isFile = Directory.Exists(Path.Combine(this.repoDirectory, ".git/rebase-apply"));

            return isFile || Directory.Exists(Path.Combine(this.repoDirectory, ".git/rebase-merge"));
        }

        /// <inheritdoc />
        public bool IsWorkingDirectoryDirty()
        {
            using (Repository repository = this.GetRepository())
            {
                return repository.RetrieveStatus().IsDirty;
            }
        }

        /// <inheritdoc />
        public bool HasConflicts()
        {
            using (Repository repository = this.GetRepository())
            {
                return repository.Index.IsFullyMerged == false;
            }
        }

        /// <inheritdoc />
        public async Task<GitCommandResponse> Squash(CancellationToken token, string newCommitMessage, GitCommit startCommit)
        {
            using (Repository repository = this.GetRepository())
            {
                if (repository.RetrieveStatus().IsDirty)
                {
                    return new GitCommandResponse(false, "Cannot rebase: You have unstaged changes.");
                }
            }

            string rewriterName;
            string commentWriterName;
            if (this.GetWritersName(out rewriterName, out commentWriterName) == false)
            {
                return new GitCommandResponse(false, "Cannot get valid paths to GIT parameters");
            }

            string fileName = Path.GetTempFileName();
            File.WriteAllText(fileName, newCommitMessage);

            var environmentVariables = new Dictionary<string, string> { { "COMMENT_FILE_NAME", fileName } };

            return await this.RunGit($"-c core.quotepath=false -c \"sequence.editor=\'{rewriterName}\'\" -c \"core.editor=\'{commentWriterName}\'\" rebase -i  {startCommit.Sha}", token, environmentVariables);
        }

        /// <inheritdoc />
        public Task<GitCommandResponse> FetchOrigin(CancellationToken token)
        {
            Task<GitCommandResponse> response = this.RunGit("fetch -v origin", token);

            return response;
        }

        /// <inheritdoc />
        public async Task<GitCommandResponse> Rebase(CancellationToken token, GitBranch parentBranch)
        {
            using (Repository repository = this.GetRepository())
            {
                if (repository.RetrieveStatus().IsDirty)
                {
                    return new GitCommandResponse(false, "Cannot rebase: You have unstaged changes.");
                }
            }

            GitCommandResponse response = await this.FetchOrigin(token);

            if (response.Success == false)
            {
                return response;
            }

            return await this.RunGit($"rebase  {parentBranch.FriendlyName}", token);
        }

        /// <inheritdoc />
        public Task<GitCommandResponse> Abort(CancellationToken token)
        {
            return this.RunGit("rebase --abort", token);
        }

        /// <inheritdoc />
        public async Task<GitCommandResponse> Continue(CancellationToken token)
        {
            string rewriterName;
            string commentWriterName;
            if (this.GetWritersName(out rewriterName, out commentWriterName) == false)
            {
                return new GitCommandResponse(false, "Cannot get valid paths to GIT parameters");
            }

            return await this.RunGit($"-c core.quotepath=false -c \"core.editor=\'{commentWriterName}\'\"  rebase --continue", token);
        }

        /// <inheritdoc />
        public IList<GitBranch> GetBranches()
        {
            using (Repository repository = this.GetRepository())
            {
                return repository.Branches.OrderBy(x => x.FriendlyName).Select(x => new GitBranch(x.FriendlyName)).ToList();
            }
        }

        private static string TrimEmptyLines(string input)
        {
            input = input.Trim('\r', '\n');
            input = input.Trim();

            return input;
        }

        private static Process CreateGitProcess(string arguments, string repoDirectory)
        {
            string gitInstallationPath = GitHelper.GetGitInstallationPath();
            string pathToGit = Path.Combine(Path.Combine(gitInstallationPath, "bin\\git.exe"));
            return new Process { StartInfo = { CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, FileName = pathToGit, Arguments = arguments, WorkingDirectory = repoDirectory }, EnableRaisingEvents = true };
        }

        private static Task<int> RunProcessAsync(Process process, CancellationToken token)
        {
            TaskCompletionSource<int> tcs = new TaskCompletionSource<int>();

            token.Register(() =>
            {
                process.Kill();
                outputLogger.WriteLine(Resources.KillingProcess);
            });

            process.Exited += (s, ea) => tcs.SetResult(process.ExitCode);

            bool started = process.Start();
            if (!started)
            {
                // you may allow for the process to be re-used (started = false) 
                // but I'm not sure about the guarantees of the Exited event in such a case
                throw new InvalidOperationException("Could not start process: " + process);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return tcs.Task;
        }

        private Repository GetRepository()
        {
            return new Repository(this.repoDirectory);
        }

        private bool GetWritersName(out string rebaseWriter, out string commentWriter)
        {
            rebaseWriter = null;
            commentWriter = null;

            try
            {

                string location = Assembly.GetExecutingAssembly().Location;

                if (string.IsNullOrWhiteSpace(location))
                {
                    return false;
                }

                string directoryName = Path.GetDirectoryName(location);

                if (string.IsNullOrWhiteSpace(directoryName))
                {
                    return false;
                }

                rebaseWriter = Path.Combine(directoryName, "rebasewriter.exe");
                commentWriter = Path.Combine(directoryName, "commentWriter.exe");
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (PathTooLongException)
            {
                return false;
            }
        }

        private async Task<GitCommandResponse> RunGit(string gitArguments, CancellationToken token, IDictionary<string, string> extraEnvironmentVariables = null, [CallerMemberName] string callerMemberName = null)
        {
            outputLogger.WriteLine($"execute: git {gitArguments}");
            error = new StringBuilder();
            output = new StringBuilder();

            using (Process process = CreateGitProcess(gitArguments, this.repoDirectory))
            {
                if (extraEnvironmentVariables != null)
                {
                    foreach (KeyValuePair<string, string> kvp in extraEnvironmentVariables)
                    {
                        process.StartInfo.EnvironmentVariables.Add(kvp.Key, kvp.Value);
                    }
                }

                process.ErrorDataReceived += this.OnErrorReceived;
                process.OutputDataReceived += this.OnOutputDataReceived;

                int returnValue = await RunProcessAsync(process, token);

                if (returnValue == 1)
                {
                    return new GitCommandResponse(false, $"{callerMemberName} failed. See output window.");
                }

                return new GitCommandResponse(true, $"{callerMemberName} succeeded.");
            }
        }

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs dataReceivedEventArgs)
        {
            if (dataReceivedEventArgs.Data == null)
            {
                return;
            }

            output.Append(dataReceivedEventArgs.Data);
            outputLogger.WriteLine(dataReceivedEventArgs.Data);
        }

        private void OnErrorReceived(object sender, DataReceivedEventArgs dataReceivedEventArgs)
        {
            if (dataReceivedEventArgs.Data == null)
            {
                return;
            }

            if (!dataReceivedEventArgs.Data.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
            {
                outputLogger.WriteLine(dataReceivedEventArgs.Data);
                return;
            }

            error = new StringBuilder();
            error.Append(dataReceivedEventArgs.Data);
            outputLogger.WriteLine(string.Format(Resources.ErrorGit, dataReceivedEventArgs.Data));
        }
    }
}