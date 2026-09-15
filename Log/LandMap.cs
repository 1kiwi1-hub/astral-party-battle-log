using System.Collections.Generic;
using AstralPartyBattleLog.Proto;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// 맵 칸 번호 → 땅 종류.
///
/// <c>CauseOrigin.Id</c>가 land일 때 그 값은 <b>LandType이 아니라 맵 칸 번호</b>다.
/// 그대로 LandType으로 조회하면 번호가 우연히 유효 범위(1~27)에 들어 <b>조용히 엉뚱한
/// 땅 이름이 붙는다.</b> 진짜 대응표는 <c>Room.Lands</c>(필드 12,
/// <c>map&lt;int32, BaseLand&gt;</c>)에 있다.
/// </summary>
internal sealed class LandMap
{
    private readonly Dictionary<long, long> _typeByNode = new();

    public void Clear() => _typeByNode.Clear();

    public long? TypeOf(long nodeId) =>
        _typeByNode.TryGetValue(nodeId, out long type) ? type : null;

    /// <summary>Room을 담은 메시지(RunningGameS2C / StartGameS2C)에서 맵을 읽는다.</summary>
    public void Update(byte[] body)
    {
        var outer = new ProtoReader(body, 0, body.Length);
        if (!outer.NextField(out int f, out int w) || f != 1 || w != ProtoReader.WireLength) return;
        if (!outer.TryReadMessage(out var room)) return;

        while (room.NextField(out int field, out int wire))
        {
            if (field == 12 && wire == ProtoReader.WireLength)
            {
                if (!room.TryReadMessage(out var entry)) return;
                ReadEntry(entry);
            }
            else if (!room.Skip(wire))
            {
                return;
            }
        }
    }

    private void ReadEntry(ProtoReader entry)
    {
        long node = 0, type = 0;
        bool haveType = false;

        while (entry.NextField(out int field, out int wire))
        {
            if (field == 1 && wire is ProtoReader.WireFixed32 or ProtoReader.WireFixed64
                                   or ProtoReader.WireVarint)
            {
                if (!entry.TryReadNumber(wire, out node)) return;
            }
            else if (field == 2 && wire == ProtoReader.WireLength)
            {
                // BaseLand {1: nodeId, 2: landType}
                if (!entry.TryReadMessage(out var land)) return;
                while (land.NextField(out int lf, out int lw))
                {
                    if (lw is ProtoReader.WireFixed32 or ProtoReader.WireFixed64 or ProtoReader.WireVarint)
                    {
                        if (!land.TryReadNumber(lw, out long v)) break;
                        if (lf == 1 && node == 0) node = v;
                        else if (lf == 2) { type = v; haveType = true; }
                    }
                    else if (!land.Skip(lw)) break;
                }
            }
            else if (!entry.Skip(wire))
            {
                return;
            }
        }

        if (node != 0 && haveType) _typeByNode[node] = type;
    }
}
