using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Runtime;

/// <summary>
/// 会话事件存储与广播（单例）：Agent 运行产生的详细事件（思考 / 工具调用 / 文本流）以 JSONL 追加写入日志目录，
/// 不写数据库；同时通过内存通道实时广播给 SSE 订阅者。事件序号 seq 按会话单调递增，支持断线后按 seq 补发。
/// </summary>
public class SessionEventStore(StoragePaths paths)
{
    private readonly ConcurrentDictionary<string, object> _locks = new();
    private readonly ConcurrentDictionary<string, long> _seq = new();
    private readonly ConcurrentDictionary<string, List<Channel<JsonObject>>> _subs = new();

    /// <summary>初始化会话的序号计数器：取数据库记录值与日志文件末行 seq 的较大者（进程崩溃后日志可能领先于数据库）。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="createdAt">会话创建时间（决定日志文件所在月份目录）。</param>
    /// <param name="dbSeq">数据库中记录的最新序号。</param>
    public void Init(string sessionId, DateTime createdAt, long dbSeq)
    {
        _seq.GetOrAdd(sessionId, _ =>
        {
            var max = dbSeq; var file = paths.RunEventLog(sessionId, createdAt);
            if (File.Exists(file))
            {
                var last = File.ReadLines(file).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (last != null) try { max = Math.Max(max, JsonNode.Parse(last)?["seq"]?.GetValue<long>() ?? 0); } catch { /* 末行不完整忽略 */ }
            }
            return max;
        });
    }

    /// <summary>删除会话的事件日志文件并清理内存状态。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="createdAt">会话创建时间。</param>
    public void Delete(string sessionId, DateTime createdAt)
    {
        var file = paths.RunEventLog(sessionId, createdAt);
        if (File.Exists(file)) File.Delete(file);
        _seq.TryRemove(sessionId, out _); _locks.TryRemove(sessionId, out _);
        if (_subs.TryRemove(sessionId, out var subscriptions)) lock (subscriptions) foreach (var ch in subscriptions) ch.Writer.TryComplete();
    }

    /// <summary>当前序号。</summary>
    /// <param name="sessionId">会话 ID。</param>
    public long CurrentSeq(string sessionId) => _seq.GetValueOrDefault(sessionId);

    public void ForgetDeleted(HashSet<string> remaining)
    {
        foreach (var id in _seq.Keys.Where(id => !remaining.Contains(id))) { _seq.TryRemove(id, out _); _locks.TryRemove(id, out _); }
    }

    /// <summary>追加事件：分配 seq 与本地时间戳，写入日志文件，并广播给订阅者。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="createdAt">会话创建时间。</param>
    /// <param name="runId">运行 ID。</param>
    /// <param name="evt">事件（会被补充 seq / ts / runId）。</param>
    /// <returns>写入后的事件。</returns>
    public JsonObject Append(string sessionId, DateTime createdAt, string runId, JsonObject evt)
    {
        var gate = _locks.GetOrAdd(sessionId, _ => new object());
        lock (gate)
        {
            var seq = _seq.AddOrUpdate(sessionId, 1, (_, v) => v + 1);
            evt["seq"] = seq; evt["ts"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff"); evt["runId"] = runId;
            var file = paths.RunEventLog(sessionId, createdAt);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, evt.ToJsonString(JsonSerializerOptions.Web) + "\n", new UTF8Encoding(false));
        }
        Publish(sessionId, evt);
        return evt;
    }

    /// <summary>读取历史事件（序号大于 afterSeq，可按运行过滤）。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="createdAt">会话创建时间。</param>
    /// <param name="afterSeq">起始序号（不含）。</param>
    /// <param name="runId">仅返回该运行的事件（可空）。</param>
    /// <returns>事件列表。</returns>
    public List<JsonObject> Read(string sessionId, DateTime createdAt, long afterSeq, string? runId = null)
    {
        var file = paths.RunEventLog(sessionId, createdAt);
        var list = new List<JsonObject>();
        if (!File.Exists(file)) return list;
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? o; try { o = JsonNode.Parse(line) as JsonObject; } catch { continue; }
            if (o == null) continue;
            if ((o["seq"]?.GetValue<long>() ?? 0) <= afterSeq) continue;
            if (runId != null && o["runId"]?.GetValue<string>() != runId) continue;
            list.Add(o);
        }
        return list;
    }

    /// <summary>订阅会话的实时事件。返回的 Disposable 用于取消订阅。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <returns>通道读取器与取消订阅句柄。</returns>
    public (ChannelReader<JsonObject> Reader, IDisposable Handle) Subscribe(string sessionId)
    {
        var ch = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });
        var list = _subs.GetOrAdd(sessionId, _ => []);
        lock (list) list.Add(ch);
        return (ch.Reader, new Unsub(() => { lock (list) { list.Remove(ch); if (list.Count == 0) _subs.TryRemove(new KeyValuePair<string, List<Channel<JsonObject>>>(sessionId, list)); } ch.Writer.TryComplete(); }));
    }

    /// <summary>向订阅者发布事件（克隆一份，避免多消费者共享同一节点）。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="evt">事件。</param>
    private void Publish(string sessionId, JsonObject evt)
    {
        if (!_subs.TryGetValue(sessionId, out var list)) return;
        Channel<JsonObject>[] targets; lock (list) targets = list.ToArray();
        foreach (var ch in targets)
            if (!ch.Writer.TryWrite((JsonObject)JsonNode.Parse(evt.ToJsonString())!)) ch.Writer.TryComplete(); // slow clients reconnect from persisted seq
    }

    /// <summary>取消订阅句柄。</summary>
    /// <param name="onDispose">释放时执行的动作。</param>
    private sealed class Unsub(Action onDispose) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => onDispose();
    }
}
