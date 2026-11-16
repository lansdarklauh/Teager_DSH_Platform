"""Bind an HTTPS CONNECT tunnel to its declared TLS server name."""


def server_name(record):
    if len(record) < 9 or record[0] != 22 or record[5] != 1:
        raise ValueError('TLS ClientHello required')
    body = record[9:]
    if int.from_bytes(record[6:9], 'big') != len(body):
        raise ValueError('fragmented ClientHello is not supported')
    position = 34
    position += 1 + body[position]
    position += 2 + int.from_bytes(body[position:position + 2], 'big')
    position += 1 + body[position]
    length = int.from_bytes(body[position:position + 2], 'big')
    position += 2
    end = position + length
    if end != len(body):
        raise ValueError('invalid TLS extensions')
    while position + 4 <= end:
        kind = int.from_bytes(body[position:position + 2], 'big')
        size = int.from_bytes(body[position + 2:position + 4], 'big')
        position += 4
        if position + size > end:
            raise ValueError('invalid TLS extension')
        if kind == 0:
            data = body[position:position + size]
            if len(data) < 5 or data[2] != 0 or int.from_bytes(data[:2], 'big') != len(data) - 2 or int.from_bytes(data[3:5], 'big') != len(data) - 5:
                raise ValueError('one DNS server name required')
            return data[5:].decode('ascii').lower().rstrip('.')
        position += size
    raise ValueError('TLS server name required')


def read_hello(connection, expected_host):
    def read_exact(size):
        result = b''
        while len(result) < size:
            chunk = connection.recv(size - len(result))
            if not chunk:
                raise ValueError('incomplete TLS handshake')
            result += chunk
        return result
    connection.settimeout(15)
    header = read_exact(5)
    size = int.from_bytes(header[3:5], 'big')
    if size > 16384:
        raise ValueError('oversized TLS handshake')
    record = header + read_exact(size)
    try:
        name = server_name(record)
    except (IndexError, UnicodeError) as error:
        raise ValueError('invalid TLS handshake') from error
    if name != expected_host:
        raise ValueError('TLS server name differs from CONNECT host')
    return record
