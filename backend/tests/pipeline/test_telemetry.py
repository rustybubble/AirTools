import math

import numpy as np
import pytest

from pipeline.telemetry import TelemetryRecord, parse_srt, resample, to_enu

# --- parse_srt: bracket-tag format (not this repo's real corpus clip, see below) --

_NEW_FORMAT_SRT = """1
00:00:00,000 --> 00:00:00,033
<font size="28">FrameCnt: 1, DiffTime: 33ms
2024-05-01 10:12:13.456
[iso: 100] [shutter: 1/500.0] [fnum: 1.7] [ev: 0] [color_md: default] [focal_len: 24.00] [latitude: 33.776123] [longitude: -84.396456] [rel_alt: 30.100 abs_alt: 312.456] [ct: 5500] </font>

2
00:00:01,000 --> 00:00:01,033
<font size="28">FrameCnt: 31, DiffTime: 33ms
2024-05-01 10:12:14.456
[iso: 100] [shutter: 1/500.0] [fnum: 1.7] [ev: 0] [color_md: default] [focal_len: 24.00] [latitude: 33.776200] [longitude: -84.396400] [rel_alt: 30.500 abs_alt: 312.856] [ct: 5500] </font>
"""


def test_parse_srt_new_format():
    records = parse_srt(_NEW_FORMAT_SRT)
    assert len(records) == 2
    r0 = records[0]
    assert r0.t_s == pytest.approx(0.0)
    assert r0.lat == pytest.approx(33.776123)
    assert r0.lon == pytest.approx(-84.396456)
    assert r0.rel_alt_m == pytest.approx(30.100)
    assert r0.abs_alt_m == pytest.approx(312.456)
    assert records[1].t_s == pytest.approx(1.0)


# --- parse_srt: single-line OSD format (the real Mini 4K format, with a GPS fix) --------------

_OLD_FORMAT_SRT = """1
00:00:00,000 --> 00:00:00,100
F/2.8, SS 320, ISO 100, EV 0, GPS (-84.3964, 33.7761, 19), D 12.34m, H 30.10m, H.S 0.00m/s, V.S 0.00m/s

2
00:00:01,000 --> 00:00:01,100
F/2.8, SS 320, ISO 100, EV 0, GPS (-84.3960, 33.7765, 20), D 12.50m, H 31.00m, H.S 0.50m/s, V.S 0.10m/s
"""


def test_parse_srt_old_format():
    records = parse_srt(_OLD_FORMAT_SRT)
    assert len(records) == 2
    r0 = records[0]
    assert r0.t_s == pytest.approx(0.0)
    assert r0.lon == pytest.approx(-84.3964)
    assert r0.lat == pytest.approx(33.7761)
    assert r0.rel_alt_m == pytest.approx(30.10)  # "H 30.10m"
    assert r0.abs_alt_m is None  # bug (c): the format has no separate absolute-altitude field
    assert r0.satellites == pytest.approx(19.0)  # GPS(...)'s 3rd value is the satellite count
    assert r0.dist_home_m == pytest.approx(12.34)  # "D 12.34m"
    assert r0.h_speed_mps == pytest.approx(0.0)
    assert r0.v_speed_mps == pytest.approx(0.0)
    assert r0.iso == pytest.approx(100.0)
    assert r0.shutter == pytest.approx(320.0)  # "SS 320"
    assert records[1].t_s == pytest.approx(1.0)
    assert records[1].satellites == pytest.approx(20.0)
    assert records[1].h_speed_mps == pytest.approx(0.50)


def test_parse_srt_skips_unrecognised_blocks():
    text = "1\n00:00:00,000 --> 00:00:00,100\njust a title card, no telemetry\n\n" + _OLD_FORMAT_SRT
    records = parse_srt(text)
    assert len(records) == 2  # the title-card block is skipped, not raised on


# --- parse_srt: real lines from vid/DJI_0095.MP4's SRT track (88s, indoors, no GPS fix at all) --
# Bug (b): these used to be dropped entirely (GPS "n/a" was treated as unparseable). Blocks 1/12/54
# picked for variety: different satellite counts, H, H.S, ISO/SS.

_REAL_DJI_0095_SRT = """1
00:00:00,000 --> 00:00:01,000
F/2.8, SS 30.15, ISO 710, EV 0, DZOOM 1.000, GPS (n/a, n/a, 4), D n/a, H 1.10m, H.S 0.00m/s, V.S -0.00m/s

12
00:00:11,000 --> 00:00:12,000
F/2.8, SS 30.15, ISO 600, EV 0, DZOOM 1.000, GPS (n/a, n/a, 3), D n/a, H 1.10m, H.S 0.30m/s, V.S -0.00m/s

54
00:00:53,000 --> 00:00:54,000
F/2.8, SS 30.15, ISO 460, EV 0, DZOOM 1.000, GPS (n/a, n/a, 0), D n/a, H 1.50m, H.S 0.61m/s, V.S -0.00m/s
"""


def test_parse_srt_real_dji_0095_keeps_gps_na_blocks():
    records = parse_srt(_REAL_DJI_0095_SRT)
    assert len(records) == 3  # bug (b): all 3 have GPS "n/a" -- used to be dropped

    r0 = records[0]
    assert math.isnan(r0.lat)
    assert math.isnan(r0.lon)
    assert r0.satellites == pytest.approx(4.0)  # bug (c): the "4" is satellites, not altitude
    assert r0.abs_alt_m is None
    assert r0.dist_home_m is None  # "D n/a"
    assert r0.rel_alt_m == pytest.approx(1.10)  # "H 1.10m" -- reported with no GPS fix
    assert r0.h_speed_mps == pytest.approx(0.0)
    assert r0.v_speed_mps == pytest.approx(-0.0)
    assert r0.iso == pytest.approx(710.0)
    assert r0.shutter == pytest.approx(30.15)  # "SS 30.15"

    assert records[1].satellites == pytest.approx(3.0)
    assert records[1].h_speed_mps == pytest.approx(0.30)
    assert records[2].satellites == pytest.approx(0.0)
    assert records[2].rel_alt_m == pytest.approx(1.50)
    assert records[2].h_speed_mps == pytest.approx(0.61)


# --- to_enu -----------------------------------------------------------------------


def test_to_enu_matches_hand_computed_offsets():
    lat0, lon0 = 33.7761, -84.3964
    records = [
        TelemetryRecord(t_s=0.0, lat=lat0, lon=lon0, rel_alt_m=10.0),
        TelemetryRecord(t_s=1.0, lat=lat0 + 0.0001, lon=lon0 + 0.0001, rel_alt_m=15.0),
    ]
    enu = to_enu(records)
    assert enu.shape == (2, 3)
    assert enu[0] == pytest.approx([0.0, 0.0, 10.0], abs=1e-6)

    R = 6_378_137.0
    expected_east = math.radians(0.0001) * R * math.cos(math.radians(lat0))
    expected_north = math.radians(0.0001) * R
    assert enu[1, 0] == pytest.approx(expected_east, rel=1e-6)
    assert enu[1, 1] == pytest.approx(expected_north, rel=1e-6)
    assert enu[1, 2] == pytest.approx(
        15.0
    )  # rel_alt_m is already height above launch -- used as-is


def test_to_enu_falls_back_to_abs_alt_when_no_rel_alt():
    records = [
        TelemetryRecord(t_s=0.0, lat=33.0, lon=-84.0, rel_alt_m=None, abs_alt_m=300.0),
        TelemetryRecord(t_s=1.0, lat=33.0, lon=-84.0, rel_alt_m=None, abs_alt_m=305.0),
    ]
    enu = to_enu(records)
    assert enu[0, 2] == pytest.approx(0.0)
    assert enu[1, 2] == pytest.approx(5.0)


def test_to_enu_all_nan_gps_still_reports_up_from_rel_alt():
    """The real DJI_0095.srt corpus clip: every record has H but no GPS fix at all -- must not
    crash (bug (b)/tolerate-missing-GPS), and H (barometer/VPS) is unaffected by the missing fix."""
    records = [
        TelemetryRecord(t_s=0.0, lat=float("nan"), lon=float("nan"), rel_alt_m=1.1),
        TelemetryRecord(t_s=1.0, lat=float("nan"), lon=float("nan"), rel_alt_m=1.0),
    ]
    enu = to_enu(records)
    assert np.isnan(enu[:, 0]).all()  # east: no fix anywhere -- can't be computed
    assert np.isnan(enu[:, 1]).all()  # north: same
    assert enu[:, 2] == pytest.approx([1.1, 1.0])  # up: rel_alt_m, unaffected by missing GPS


def test_to_enu_origin_skips_leading_records_with_no_gps_fix():
    """Default origin must be the first record with an actual fix, not blindly `records[0]` (which
    may have no fix yet, e.g. acquiring GPS after take-off) -- else every row's east/north would
    come out NaN even for later frames that do have a fix."""
    lat0, lon0 = 33.7761, -84.3964
    records = [
        TelemetryRecord(t_s=0.0, lat=float("nan"), lon=float("nan"), rel_alt_m=1.0),  # no fix yet
        TelemetryRecord(t_s=1.0, lat=lat0, lon=lon0, rel_alt_m=1.0),  # first real fix
        TelemetryRecord(t_s=2.0, lat=lat0 + 0.0001, lon=lon0, rel_alt_m=1.0),
    ]
    enu = to_enu(records)
    assert np.isnan(enu[0, 0]) and np.isnan(enu[0, 1])  # no fix that frame
    assert enu[1] == pytest.approx([0.0, 0.0, 1.0], abs=1e-6)  # origin is the first real fix


# --- resample -----------------------------------------------------------------------


def test_resample_linear_interpolation():
    records = [
        TelemetryRecord(t_s=0.0, lat=10.0, lon=20.0, rel_alt_m=1.0, abs_alt_m=100.0),
        TelemetryRecord(t_s=2.0, lat=12.0, lon=22.0, rel_alt_m=3.0, abs_alt_m=102.0),
    ]
    resampled = resample(records, [0.0, 1.0, 2.0])
    assert [r.lat for r in resampled] == pytest.approx([10.0, 11.0, 12.0])
    assert [r.lon for r in resampled] == pytest.approx([20.0, 21.0, 22.0])
    assert [r.rel_alt_m for r in resampled] == pytest.approx([1.0, 2.0, 3.0])


def test_resample_handles_duplicate_1hz_gps_positions():
    """Mini 4K writes ~30 subtitle lines/s but only updates GPS at 1 Hz -- several consecutive
    records share the exact same lat/lon. Interpolating within a duplicate run must stay put."""
    records = [
        TelemetryRecord(t_s=0.0, lat=10.0, lon=20.0, rel_alt_m=5.0),
        TelemetryRecord(t_s=0.33, lat=10.0, lon=20.0, rel_alt_m=5.0),  # duplicate GPS fix
        TelemetryRecord(t_s=0.66, lat=10.0, lon=20.0, rel_alt_m=5.0),  # duplicate GPS fix
        TelemetryRecord(t_s=1.0, lat=10.001, lon=20.001, rel_alt_m=6.0),  # next 1Hz update
    ]
    resampled = resample(records, [0.5])
    assert resampled[0].lat == pytest.approx(10.0)
    assert resampled[0].lon == pytest.approx(20.0)


def test_resample_empty_records_raises():
    with pytest.raises(ValueError):
        resample([], [0.0])


def test_resample_propagates_nan_gps_without_crashing():
    """All-NaN lat/lon (no GPS fix the whole clip, like the real corpus clip) must interpolate to
    NaN, not raise -- rel_alt_m (H) keeps interpolating normally regardless."""
    records = [
        TelemetryRecord(t_s=0.0, lat=float("nan"), lon=float("nan"), rel_alt_m=1.1),
        TelemetryRecord(t_s=1.0, lat=float("nan"), lon=float("nan"), rel_alt_m=1.0),
    ]
    resampled = resample(records, [0.0, 0.5, 1.0])
    assert all(math.isnan(r.lat) for r in resampled)
    assert all(math.isnan(r.lon) for r in resampled)
    assert [r.rel_alt_m for r in resampled] == pytest.approx([1.1, 1.05, 1.0])
