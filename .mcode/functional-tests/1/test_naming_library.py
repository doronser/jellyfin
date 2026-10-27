import os, time, shutil, pytest, requests

URL = os.environ.get("JELLYFIN_URL", "http://localhost:8096")
ROOT = "/tmp/ftmedia"
H = 'MediaBrowser Client="ft", Device="ft", DeviceId="ft-dev", Version="1.0"'
S = {}
import json
SIDE = os.environ.get("FT_SIDE", "target")


def rec(name, val):
    p = f"/tmp/ft_{SIDE}.json"
    d = json.load(open(p)) if os.path.exists(p) else {}
    d[name] = val
    json.dump(d, open(p, "w"))


def hdr(token=None):
    h = {"Authorization": "MediaBrowser " + H.replace('MediaBrowser ', '') + (f', Token="{token}"' if token else ""),
         "Content-Type": "application/json"}
    return h


@pytest.fixture(scope="module", autouse=True)
def setup():
    r = requests.get(URL + "/health", timeout=10)
    assert r.status_code == 200
    shutil.rmtree(ROOT, ignore_errors=True)
    files = {
        "movies/The Matrix (1999)/The Matrix (1999).mkv": b"x",
        "movies/Inception (2010)/Inception (2010) - 1080p.mp4": b"x",
        "tv/Breaking Bad/Season 01/Breaking Bad S01E01.mkv": b"x",
        "tv/Breaking Bad/Season 01/Breaking Bad S01E02.mkv": b"x",
        "music/Artist/Album/01 - Track One.mp3": b"x",
        "photos/holiday/IMG_0001.jpg": b"\xff\xd8\xff\xd9",
        "photos/IMG_0002.jpg": b"\xff\xd8\xff\xd9",
    }
    for p, c in files.items():
        fp = os.path.join(ROOT, p)
        os.makedirs(os.path.dirname(fp), exist_ok=True)
        open(fp, "wb").write(c)
    if not requests.get(URL + "/System/Info/Public", timeout=10).json().get("StartupWizardCompleted"):
        requests.post(URL + "/Startup/Configuration", json={"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"}, headers=hdr(), timeout=10)
        requests.get(URL + "/Startup/User", headers=hdr(), timeout=10)
        r = requests.post(URL + "/Startup/User", json={"Name": "ftadmin", "Password": "ftpass"}, headers=hdr(), timeout=10)
        assert r.status_code in (200, 204), r.text
        r = requests.post(URL + "/Startup/Complete", headers=hdr(), timeout=10)
        assert r.status_code in (200, 204)
    r = requests.post(URL + "/Users/AuthenticateByName", json={"Username": "ftadmin", "Pw": "ftpass"}, headers=hdr(), timeout=20)
    assert r.status_code == 200, r.text
    S["token"] = r.json()["AccessToken"]


def api(m, p, **kw):
    return requests.request(m, URL + p, headers=hdr(S["token"]), timeout=60, **kw)


def test_public_info():
    r = requests.get(URL + "/System/Info/Public", timeout=10)
    assert r.status_code == 200
    j = r.json()
    assert j["ProductName"] == "Jellyfin Server" and j["Version"]
    rec("public", {"ProductName": j["ProductName"], "Wizard": j.get("StartupWizardCompleted")})


def test_system_info_authed():
    r = api("GET", "/System/Info")
    assert r.status_code == 200 and r.json()["Version"]


def test_unauth_system_info_rejected():
    assert requests.get(URL + "/System/Info", timeout=10).status_code in (401, 403)


def test_not_found_item():
    r = api("GET", "/Items/00000000000000000000000000000001")
    assert r.status_code in (400, 404)


LIBS = [("FTMovies", "movies", "movies"), ("FTShows", "tvshows", "tv"), ("FTMusic", "music", "music"), ("FTPhotos", "homevideos", "photos")]


def test_create_libraries_and_scan():
    for name, ctype, d in LIBS:
        r = api("POST", "/Library/VirtualFolders", params={"name": name, "collectionType": ctype, "refreshLibrary": "false"},
                json={"LibraryOptions": {"PathInfos": [{"Path": f"{ROOT}/{d}"}], "EnableRealtimeMonitor": False,
                      "MetadataSavers": [], "TypeOptions": [], "EnableInternetProviders": False}})
        assert r.status_code in (200, 204), (name, r.text)
    r = api("POST", "/Library/Refresh")
    assert r.status_code in (200, 204)
    deadline = time.time() + 120
    while time.time() < deadline:
        time.sleep(3)
        r = api("GET", "/Items", params={"Recursive": "true", "IncludeItemTypes": "Movie", "Fields": "Path"})
        if r.json().get("TotalRecordCount", 0) >= 2:
            time.sleep(5)
            break


def items(**p):
    p["Recursive"] = "true"
    r = api("GET", "/Items", params=p)
    assert r.status_code == 200, r.text
    return r.json()["Items"]


def test_movie_naming_resolution():
    names = {i["Name"]: i for i in items(IncludeItemTypes="Movie")}
    rec("movies", sorted((n, i.get("ProductionYear")) for n, i in names.items()))
    assert "The Matrix" in names, names.keys()
    assert "Inception" in names, names.keys()
    assert names["The Matrix"].get("ProductionYear") == 1999


def test_tv_naming_resolution():
    series = items(IncludeItemTypes="Series")
    assert any(s["Name"] == "Breaking Bad" for s in series), series
    eps = items(IncludeItemTypes="Episode")
    rec("tv", [sorted(s["Name"] for s in series), sorted((e.get("ParentIndexNumber"), e.get("IndexNumber")) for e in eps)])
    pairs = {(e.get("ParentIndexNumber"), e.get("IndexNumber")) for e in eps}
    assert {(1, 1), (1, 2)} <= pairs, pairs


def test_audio_resolution():
    a = items(IncludeItemTypes="Audio")
    rec("audio", sorted(x["Name"] for x in a))
    assert len(a) >= 1, a


def test_photo_library_resolution():
    ph = items(IncludeItemTypes="Photo")
    rec("photo", sorted(p["Name"] for p in ph))
    assert len(ph) >= 2, ph
    assert {p["Name"] for p in ph} >= {"IMG_0001", "IMG_0002"} or len(ph) >= 2


def test_cleanup_libraries():
    for name, _, _ in LIBS:
        r = api("DELETE", "/Library/VirtualFolders", params={"name": name})
        assert r.status_code in (200, 204)
