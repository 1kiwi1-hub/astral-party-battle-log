namespace AstralPartyBattleLog.Proto;

/// <summary>
/// 최소 protobuf 워이어 포맷 리더.
///
/// 게임의 protobuf 파서를 쓰지 않고 직접 읽는다. 이유는 성능이 아니라 안전이다 —
/// 여기에 없는 메시지는 디코딩할 코드가 존재하지 않으므로, 손패 같은 내용을
/// "실수로" 읽을 방법이 없다. 모르는 필드는 길이만 보고 건너뛴다.
/// </summary>
internal struct ProtoReader
{
    public const int WireVarint = 0;
    public const int WireFixed64 = 1;
    public const int WireLength = 2;
    public const int WireFixed32 = 5;

    private readonly byte[] _buf;
    private int _pos;
    private readonly int _end;

    public ProtoReader(byte[] buf, int offset, int length)
    {
        _buf = buf;
        _pos = offset;
        _end = offset + length;
    }

    public bool HasMore => _pos < _end;

    /// <summary>다음 태그를 읽는다. 스트림이 끝났거나 손상되면 false.</summary>
    public bool NextField(out int fieldNumber, out int wireType)
    {
        fieldNumber = 0;
        wireType = 0;
        if (_pos >= _end) return false;
        if (!TryReadVarint(out ulong tag)) return false;
        fieldNumber = (int)(tag >> 3);
        wireType = (int)(tag & 0x7);
        return fieldNumber > 0;
    }

    public bool TryReadVarint(out ulong value)
    {
        value = 0;
        int shift = 0;
        while (_pos < _end && shift <= 63)
        {
            byte b = _buf[_pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }


    /// <summary>
    /// 숫자 필드 하나를 wire type에 맞게 읽는다.
    ///
    /// 이 게임의 .proto는 숫자를 대부분 <c>sfixed32</c>/<c>sfixed64</c>로 선언했고
    /// (enum만 varint), 그래서 varint만 읽으면 값이 전부 0으로 나온다. 실측으로
    /// 확인된 사실이라 세 가지를 모두 지원한다.
    /// </summary>
    public bool TryReadNumber(int wireType, out long value)
    {
        switch (wireType)
        {
            case WireVarint:
                bool ok = TryReadVarint(out ulong v);
                value = (long)v;
                return ok;
            case WireFixed32:
                return TryReadFixed32(out value);
            case WireFixed64:
                return TryReadFixed64(out value);
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>little-endian 4바이트. sfixed32로 보고 부호 확장한다.</summary>
    public bool TryReadFixed32(out long value)
    {
        value = 0;
        if (_end - _pos < 4) return false;
        int raw = _buf[_pos] | (_buf[_pos + 1] << 8) | (_buf[_pos + 2] << 16) | (_buf[_pos + 3] << 24);
        _pos += 4;
        value = raw; // int이므로 음수는 그대로 부호 확장된다
        return true;
    }

    /// <summary>little-endian 8바이트.</summary>
    public bool TryReadFixed64(out long value)
    {
        value = 0;
        if (_end - _pos < 8) return false;
        ulong raw = 0;
        for (int i = 7; i >= 0; i--) raw = (raw << 8) | _buf[_pos + i];
        _pos += 8;
        value = (long)raw;
        return true;
    }

    /// <summary>length-delimited 필드의 범위를 돌려준다. 내용은 복사하지 않는다.</summary>
    public bool TryReadLengthDelimited(out int offset, out int length)
    {
        offset = 0;
        length = 0;
        if (!TryReadVarint(out ulong len)) return false;
        if (len > (ulong)(_end - _pos)) return false;
        offset = _pos;
        length = (int)len;
        _pos += length;
        return true;
    }

    /// <summary>UTF-8 문자열 필드.</summary>
    public bool TryReadString(out string value)
    {
        if (TryReadLengthDelimited(out int off, out int len))
        {
            value = System.Text.Encoding.UTF8.GetString(_buf, off, len);
            return true;
        }
        value = "";
        return false;
    }

    /// <summary>중첩 메시지를 읽기 위한 하위 리더.</summary>
    public bool TryReadMessage(out ProtoReader sub)
    {
        if (TryReadLengthDelimited(out int off, out int len))
        {
            sub = new ProtoReader(_buf, off, len);
            return true;
        }
        sub = default;
        return false;
    }

    /// <summary>관심 없는 필드를 건너뛴다. 알 수 없는 wire type이면 false(=파싱 중단).</summary>
    public bool Skip(int wireType)
    {
        switch (wireType)
        {
            case WireVarint:
                return TryReadVarint(out _);
            case WireFixed64:
                return Advance(8);
            case WireLength:
                return TryReadLengthDelimited(out _, out _);
            case WireFixed32:
                return Advance(4);
            default:
                return false; // group(3,4)은 이 프로토콜에서 안 쓰임
        }
    }

    private bool Advance(int n)
    {
        if (_end - _pos < n) return false;
        _pos += n;
        return true;
    }
}
