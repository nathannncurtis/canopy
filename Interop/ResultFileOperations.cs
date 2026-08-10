namespace SizeMonitor.Interop;

using System.Diagnostics;
using System.ComponentModel;

public enum ResultFileOperationKind { Rename, Move, Compress, Decompress, CreateZip }
public enum ResultFileOperationStatus { Succeeded, Failed, Cancelled }
public sealed record ResultFileOperationRequest(ResultFileOperationKind Kind,
    IReadOnlyList<string> Sources, string? Destination = null);
public sealed record ResultFileSourceSnapshot(string Identity, bool IsDirectory, long Length, DateTime LastWriteUtc);
public sealed record ResultFileMemberSnapshot(string RelativePath, ResultFileSourceSnapshot Snapshot);
public sealed record ResultFileOperationPlanItem(string Source, string? Destination,
    ResultFileSourceSnapshot? Snapshot, IReadOnlyList<ResultFileMemberSnapshot> Members, string? Error);
public sealed record ResultFileOperationPreview(Guid ConfirmationId, ResultFileOperationRequest Request,
    IReadOnlyList<ResultFileOperationPlanItem> Items, bool CanExecute);
public sealed record ResultFileOperationOutcome(string Source, string? Destination,
    ResultFileOperationStatus Status, string? Error = null);
public sealed record ResultFileOperationReport(IReadOnlyList<ResultFileOperationOutcome> Outcomes)
{
    public int Succeeded => Outcomes.Count(item => item.Status == ResultFileOperationStatus.Succeeded);
    public int Failed => Outcomes.Count(item => item.Status == ResultFileOperationStatus.Failed);
    public int Cancelled => Outcomes.Count(item => item.Status == ResultFileOperationStatus.Cancelled);
}

public interface IResultFileOperationBackend
{
    bool Exists(string path);
    bool IsDirectory(string path);
    ResultFileSourceSnapshot Capture(string path);
    IReadOnlyList<ResultFileMemberSnapshot> CaptureMembers(string directory);
    Task MoveAsync(string source, string destination, CancellationToken token);
    Task SetCompressionAsync(string path, bool compressed, CancellationToken token);
    Task CreateZipAsync(IReadOnlyList<string> sources, string destination, CancellationToken token);
}

public sealed record ExternalProcessRequest(string FileName, IReadOnlyList<string> Arguments);
public interface IResultOperationProcessRunner
{
    Task<(int ExitCode, string Error)> RunAsync(ExternalProcessRequest request, CancellationToken token);
}
public sealed class ResultOperationProcessRunner : IResultOperationProcessRunner
{
    public async Task<(int ExitCode, string Error)> RunAsync(ExternalProcessRequest request, CancellationToken token)
    {
        var start = new ProcessStartInfo(request.FileName) { UseShellExecute = false, RedirectStandardError = true,
            RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (string argument in request.Arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{request.FileName} did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, error); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            throw;
        }
        await Task.WhenAll(output, error);
        return (process.ExitCode, error.Result);
    }
}
public static class NtfsCompressionPlan
{
    public static ExternalProcessRequest Create(string path, bool directory, bool compress, bool supportsCompression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!supportsCompression) throw new NotSupportedException("The target volume does not support NTFS compression.");
        var arguments = new List<string> { compress ? "/C" : "/U", "/Q" };
        if (directory) arguments.Add("/S:" + path);
        arguments.Add(path);
        return new("compact.exe", arguments);
    }
}
public sealed record ResultTraversalEntry(string FullPath, string RelativePath, bool IsDirectory, bool IsReparsePoint = false);
public interface IResultFileTraversal
{
    IEnumerable<ResultTraversalEntry> Enumerate(string root);
}
public sealed class ReparseSafeResultFileTraversal : IResultFileTraversal
{
    public IEnumerable<ResultTraversalEntry> Enumerate(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(child);
                bool reparse = (attributes & FileAttributes.ReparsePoint) != 0;
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                yield return new(child, Path.GetRelativePath(root, child), isDirectory, reparse);
                if (isDirectory && !reparse) pending.Push(child);
            }
        }
    }
}
public interface IResultFileMutation
{
    void CreateDirectory(string path);
    void CopyFile(string source, string destination);
    void MoveDirectory(string source, string destination);
    void DeleteDirectory(string path, bool recursive);
    bool DirectoryExists(string path);
    ResultFileMetadata CaptureMetadata(string path);
    void ApplyMetadata(string path, ResultFileMetadata metadata);
}
public sealed record ResultFileMetadata(FileAttributes Attributes, DateTime LastWriteUtc);
public static class DirectoryMoveRouting
{
    public static bool RequiresStaging(string source, string destination)
    {
        string sourceRoot = Path.GetPathRoot(Path.GetFullPath(source)) ?? string.Empty;
        string destinationRoot = Path.GetPathRoot(Path.GetFullPath(destination)) ?? string.Empty;
        return !sourceRoot.Equals(destinationRoot, StringComparison.OrdinalIgnoreCase);
    }
}
public static class StagedDirectoryMove
{
    public static Task ExecuteAsync(string source, string destination, string staging,
        IResultFileTraversal traversal, IResultFileMutation files, CancellationToken token)
    {
        try
        {
            files.CreateDirectory(staging);
            ResultFileMetadata rootMetadata = files.CaptureMetadata(source);
            var directories = new List<(string Path, ResultFileMetadata Metadata)>();
            foreach (ResultTraversalEntry entry in traversal.Enumerate(source))
            {
                token.ThrowIfCancellationRequested();
                if (entry.IsReparsePoint) throw new IOException("Cross-volume moves do not follow or copy reparse points.");
                string target = Path.Combine(staging, entry.RelativePath);
                ResultFileMetadata metadata = files.CaptureMetadata(entry.FullPath);
                if (entry.IsDirectory) { files.CreateDirectory(target); directories.Add((target, metadata)); }
                else { files.CreateDirectory(Path.GetDirectoryName(target)!); files.CopyFile(entry.FullPath, target); files.ApplyMetadata(target, metadata); }
            }
            foreach ((string path, ResultFileMetadata metadata) in directories.AsEnumerable().Reverse()) files.ApplyMetadata(path, metadata);
            files.ApplyMetadata(staging, rootMetadata);
            files.MoveDirectory(staging, destination);
            files.DeleteDirectory(source, recursive: true);
            return Task.CompletedTask;
        }
        catch
        {
            try { if (files.DirectoryExists(staging)) files.DeleteDirectory(staging, recursive: true); } catch (IOException) { }
            throw;
        }
    }
}

public sealed class ResultFileOperationService(IResultFileOperationBackend backend)
{
    public ResultFileOperationPreview Preview(ResultFileOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var items = new List<ResultFileOperationPlanItem>();
        string[] sources = request.Sources.Select(Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string? destination = string.IsNullOrWhiteSpace(request.Destination) ? null : Canonical(request.Destination);
        if (request.Kind == ResultFileOperationKind.Rename && sources.Length != 1)
            return Invalid(request, sources, "Rename requires exactly one selected item.");
        if (request.Kind is ResultFileOperationKind.Rename or ResultFileOperationKind.Move or ResultFileOperationKind.CreateZip && destination is null)
            return Invalid(request, sources, "A destination is required.");
        if (request.Kind == ResultFileOperationKind.CreateZip && !destination!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return Invalid(request, sources, "The archive destination must end in .zip.");
        if (request.Kind == ResultFileOperationKind.Move && !backend.IsDirectory(destination!))
            return Invalid(request, sources, "The move destination must be an existing directory.");
        foreach (string source in sources)
        {
            string? target = request.Kind switch
            {
                ResultFileOperationKind.Rename => destination,
                ResultFileOperationKind.Move => Path.Combine(destination!, Path.GetFileName(source)),
                ResultFileOperationKind.CreateZip => destination,
                _ => null,
            };
            ResultFileSourceSnapshot? snapshot = null;
            try { if (backend.Exists(source)) snapshot = backend.Capture(source); } catch (IOException) { }
            IReadOnlyList<ResultFileMemberSnapshot> members = snapshot?.IsDirectory == true && request.Kind == ResultFileOperationKind.CreateZip
                ? backend.CaptureMembers(source) : [];
            string? error = snapshot is null ? "Source is stale or unavailable." :
                target is not null && backend.Exists(target) && !CaseOnlyRename(request.Kind, source, target) ? "Destination already exists." :
                request.Kind == ResultFileOperationKind.Rename && !backend.IsDirectory(Path.GetDirectoryName(target!)!)
                    ? "The destination directory does not exist." :
                request.Kind == ResultFileOperationKind.Move && snapshot.IsDirectory && IsWithin(target!, source)
                    ? "A directory cannot be moved into itself or one of its descendants." :
                request.Kind == ResultFileOperationKind.CreateZip && backend.IsDirectory(source) && IsWithin(destination!, source)
                    ? "The ZIP destination cannot be inside a selected directory." : null;
            items.Add(new(source, target, snapshot, members, error));
        }
        if (request.Kind == ResultFileOperationKind.CreateZip)
            for (int left = 0; left < items.Count; left++) for (int right = left + 1; right < items.Count; right++)
            {
                bool overlap = items[left].Snapshot?.IsDirectory == true && IsWithin(items[right].Source, items[left].Source) ||
                    items[right].Snapshot?.IsDirectory == true && IsWithin(items[left].Source, items[right].Source);
                if (!overlap) continue;
                items[left] = items[left] with { Error = "Do not select both a directory and one of its descendants." };
                items[right] = items[right] with { Error = "Do not select both a directory and one of its descendants." };
            }
        foreach (IGrouping<string?, ResultFileOperationPlanItem> collision in items.Where(item => item.Destination is not null)
                     .GroupBy(item => item.Destination, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            foreach (ResultFileOperationPlanItem item in collision.ToArray())
                items[items.IndexOf(item)] = item with { Error = "Multiple selected items resolve to the same destination." };
        if (sources.Length == 0) items.Add(new(string.Empty, destination, null, [], "Select at least one item."));
        return new(Guid.NewGuid(), request with { Sources = sources, Destination = destination }, items,
            items.Count > 0 && items.All(item => item.Error is null));
    }

    public async Task<ResultFileOperationReport> ExecuteAsync(ResultFileOperationPreview preview,
        Guid confirmationId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (confirmationId == Guid.Empty || confirmationId != preview.ConfirmationId)
            throw new InvalidOperationException("The reviewed operation was not confirmed.");
        if (!preview.CanExecute) throw new InvalidOperationException("The operation preview contains errors.");
        var outcomes = new List<ResultFileOperationOutcome>();
        if (preview.Request.Kind == ResultFileOperationKind.CreateZip)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                foreach (ResultFileOperationPlanItem item in preview.Items)
                {
                    ResultFileSourceSnapshot current = backend.Capture(item.Source);
                    if (item.Snapshot is null || current != item.Snapshot)
                        throw new IOException("Source identity or metadata changed after preview; refresh before continuing.");
                    if (current.IsDirectory && !backend.CaptureMembers(item.Source).SequenceEqual(item.Members))
                        throw new IOException("Directory contents changed after preview; refresh before continuing.");
                }
                await backend.CreateZipAsync(preview.Items.Select(item => item.Source).ToArray(),
                    preview.Request.Destination!, token);
                outcomes.AddRange(preview.Items.Select(item => new ResultFileOperationOutcome(item.Source,
                    preview.Request.Destination, ResultFileOperationStatus.Succeeded)));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { outcomes.AddRange(preview.Items.Select(item => new ResultFileOperationOutcome(item.Source, item.Destination, ResultFileOperationStatus.Cancelled))); }
            catch (Exception ex) when (Expected(ex))
            { outcomes.AddRange(preview.Items.Select(item => new ResultFileOperationOutcome(item.Source, item.Destination, ResultFileOperationStatus.Failed, ex.Message))); }
            return new(outcomes);
        }
        foreach (ResultFileOperationPlanItem item in preview.Items)
        {
            if (token.IsCancellationRequested)
            { outcomes.Add(new(item.Source, item.Destination, ResultFileOperationStatus.Cancelled)); continue; }
            try
            {
                if (!backend.Exists(item.Source)) throw new FileNotFoundException("Source became unavailable.", item.Source);
                ResultFileSourceSnapshot current = backend.Capture(item.Source);
                if (item.Snapshot is null || current != item.Snapshot)
                    throw new IOException("Source identity or metadata changed after preview; refresh before continuing.");
                if (item.Destination is not null && backend.Exists(item.Destination) &&
                    !CaseOnlyRename(preview.Request.Kind, item.Source, item.Destination)) throw new IOException("Destination now exists.");
                if (preview.Request.Kind is ResultFileOperationKind.Rename or ResultFileOperationKind.Move)
                    await backend.MoveAsync(item.Source, item.Destination!, token);
                else await backend.SetCompressionAsync(item.Source,
                    preview.Request.Kind == ResultFileOperationKind.Compress, token);
                outcomes.Add(new(item.Source, item.Destination, ResultFileOperationStatus.Succeeded));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { outcomes.Add(new(item.Source, item.Destination, ResultFileOperationStatus.Cancelled)); }
            catch (Exception ex) when (Expected(ex))
            { outcomes.Add(new(item.Source, item.Destination, ResultFileOperationStatus.Failed, ex.Message)); }
        }
        return new(outcomes);
    }

    static ResultFileOperationPreview Invalid(ResultFileOperationRequest request, IEnumerable<string> sources, string error) =>
        new(Guid.NewGuid(), request, sources.DefaultIfEmpty(string.Empty).Select(source => new ResultFileOperationPlanItem(source, request.Destination, null, [], error)).ToArray(), false);
    static string Canonical(string path) { ArgumentException.ThrowIfNullOrWhiteSpace(path); return Path.GetFullPath(path); }
    static bool IsWithin(string path, string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
    static bool CaseOnlyRename(ResultFileOperationKind kind, string source, string destination) =>
        kind == ResultFileOperationKind.Rename && !source.Equals(destination, StringComparison.Ordinal) &&
        source.Equals(destination, StringComparison.OrdinalIgnoreCase);
    static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or
        InvalidOperationException or NotSupportedException or Win32Exception;
}
