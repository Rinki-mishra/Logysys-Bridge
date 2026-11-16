using System.Collections.Concurrent;
using System.Threading.Channels;

namespace LogisysOrgConverter.Logisys;

public sealed record SoftlinkPipelineJobStatus(
    Guid RunId,
    string State,
    string? Message,
    DateTime UpdatedAtUtc);

/// <summary>
/// Phase B: queue of conversion runs waiting for the Softlink pipeline.
/// Does not change Softlink step logic — only schedules SoftlinkPipeline.RunAsync.
/// </summary>
public sealed class SoftlinkPipelineQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly ConcurrentDictionary<Guid, SoftlinkPipelineJobStatus> _status = new();

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Enqueue(Guid runId)
    {
        _status[runId] = new SoftlinkPipelineJobStatus(runId, "Queued", null, DateTime.UtcNow);
        _channel.Writer.TryWrite(runId);
    }

    public SoftlinkPipelineJobStatus? GetStatus(Guid runId) =>
        _status.TryGetValue(runId, out var status) ? status : null;

    public void SetRunning(Guid runId, string? message = null) =>
        _status[runId] = new SoftlinkPipelineJobStatus(
            runId,
            "Running",
            message ?? "Opening Softlink Edge if needed, then Upload → Created → Finish → India G/L. Enter Security Code in Edge when the login page appears.",
            DateTime.UtcNow);

    public void SetSucceeded(Guid runId, string message) =>
        _status[runId] = new SoftlinkPipelineJobStatus(runId, "Succeeded", message, DateTime.UtcNow);

    public void SetFailed(Guid runId, string message) =>
        _status[runId] = new SoftlinkPipelineJobStatus(runId, "Failed", message, DateTime.UtcNow);
}
