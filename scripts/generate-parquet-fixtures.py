"""Generates the synthetic Parquet fixtures used by the PS-0.10E tests with Apache Arrow (pyarrow),
an implementation independent of the Parquet.Net reader under test. Deterministic; no real data.

Usage (from the repository root):
  docker run --rm -v "${PWD}:/work" -w /work python:3.12-slim sh -c "pip install -q pyarrow==18.1.0 && python scripts/generate-parquet-fixtures.py"
"""
import datetime as dt
import decimal
import pathlib

import pyarrow as pa
import pyarrow.parquet as pq

OUT = pathlib.Path("tests/ProofShift.Connectors.RemoteStorage.Tests/fixtures/parquet")
OUT.mkdir(parents=True, exist_ok=True)
D = decimal.Decimal


def write(name, table, **kwargs):
    pq.write_table(table, OUT / name, **kwargs)


# Rich flat schema, two row groups, exact decimals, dates, UTC and local timestamps, binary, nulls.
rows = 7
table = pa.table(
    {
        "id": pa.array(range(1, rows + 1), pa.int64()),
        "small": pa.array([1, -2, 3, None, 5, 6, -7], pa.int16()),
        "name": pa.array(["alpha", "ünï ✓", "with space", None, "", "e\u0301", "z"], pa.string()),
        "amount": pa.array(
            [D("0.01"), D("12345678901234567890.123456"), D("-99.99"), None, D("0"), D("1.10"), D("7")],
            pa.decimal128(28, 6),
        ),
        "flag": pa.array([True, False, True, None, False, True, True], pa.bool_()),
        "born": pa.array(
            [dt.date(1980, 2, 29), dt.date(1999, 12, 31), None, dt.date(2000, 1, 1), dt.date(1, 1, 1), dt.date(2024, 2, 29), dt.date(9999, 12, 31)],
            pa.date32(),
        ),
        "seen_utc": pa.array(
            [dt.datetime(2025, 3, 4, 5, 6, 7, 123456, tzinfo=dt.timezone.utc)] * rows, pa.timestamp("us", tz="UTC")
        ),
        "seen_local": pa.array([dt.datetime(2025, 3, 4, 5, 6, 7, 123456)] * rows, pa.timestamp("us")),
        "ratio32": pa.array([0.1, 1.5, None, 3.25, 1e10, -0.0, 7.0], pa.float32()),
        "ratio64": pa.array([0.1, 1.5, None, 3.25, 1e100, -0.0, 7.0], pa.float64()),
        "blob": pa.array([b"\x00\x01\xfe\xff", b"", None, b"abc", b"\x00" * 10, b"\xff", b"x"], pa.binary()),
        "u64": pa.array([0, 1, 2, 3, 4, 5, 2**63 - 1], pa.uint64()),
    }
)
write("rich.parquet", table, row_group_size=4, compression="snappy")
write("rich-gzip.parquet", table, row_group_size=3, compression="gzip")
write("rich-nodict.parquet", table, row_group_size=7, compression="none", use_dictionary=False)

# Duplicate identity.
write("duplicate.parquet", pa.table({"id": pa.array([1, 2, 2], pa.int64()), "v": pa.array(["a", "b", "c"])}))
# Null identity.
write("nullid.parquet", pa.table({"id": pa.array([1, None], pa.int64()), "v": pa.array(["a", "b"])}))

# Nested / unsupported types.
write(
    "nested.parquet",
    pa.table({"id": pa.array([1, 2], pa.int64()), "items": pa.array([[1, 2], [3]], pa.list_(pa.int64()))}),
)
write(
    "struct.parquet",
    pa.table({"id": pa.array([1], pa.int64()), "s": pa.array([{"a": 1}], pa.struct([("a", pa.int64())]))}),
)
write(
    "bigdecimal.parquet",
    pa.table({"id": pa.array([1], pa.int64()), "d": pa.array([D("1.5")], pa.decimal128(38, 2))}),
)
write(
    "nanos.parquet",
    pa.table({"id": pa.array([1], pa.int64()), "t": pa.array([dt.datetime(2025, 1, 1)], pa.timestamp("ns"))}),
)

# Many small row groups.
count = 5000
write(
    "manygroups.parquet",
    pa.table({"id": pa.array(range(count), pa.int64()), "text": pa.array([f"row-{i:05d}" for i in range(count)])}),
    row_group_size=250,
)

# Schema variants for drift detection.
write("drift-a.parquet", pa.table({"id": pa.array([1], pa.int64()), "v": pa.array(["a"])}))
write("drift-b.parquet", pa.table({"id": pa.array([1], pa.int64()), "v": pa.array([1], pa.int64())}))
print("generated", sorted(path.name for path in OUT.iterdir()))
