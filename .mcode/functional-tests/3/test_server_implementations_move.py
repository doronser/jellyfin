import json, os, shutil, time, pytest, requests

URL = os.environ.get("JELLYFIN_URL", "http://localhost:8096")
SIDE = os.environ.get("FT_SIDE", "target")
ROOT = "/tmp/ftmedia3"
AUTHV = 'Client="ft", Device="ft", DeviceId="ft-dev3", Version="1.0"'
S = {}
OUT = f"/tmp/ft3_{SIDE}.json"


def rec(name, status, body):
    d = json.load(open(OUT)) if os.path.exists(OUT) else {}
    d[name] = {"status": status, "body": body}
    json.dump(d, open(OUT, "w"), sort_keys=True)


def hdr(token=None):
    return {"Authorization": "MediaBrowser " + AUTHV + (f', Token="{token}"' if token else ""),
            "Content-Type": "application/json"}


def api(m, p, **kw):
    return requests.request(m, URL + p, headers=hdr(S.get("token")), timeout=60, **kw)


def items(**p):
    p["Recursive"] = "true"
    r = api("GET", "/Items", params=p)
    assert r.status_code == 200, r.text
    return r.json()["Items"]


@pytest.fixture(scope="module", autouse=True)
def setup():
    if os.path.exists(OUT):
        os.remove(OUT)
    assert requests.get(URL + "/health", timeout=10).status_code == 200
    deadline = time.time() + 180
    while time.time() < deadline:
        if requests.get(URL + "/Startup/Configuration", headers=hdr(), timeout=10).status_code != 503:
            break
        time.sleep(2)
    shutil.rmtree(ROOT, ignore_errors=True)
    for p in ["movies/The Matrix (1999)/The Matrix (1999).mkv", "movies/Inception (2010)/Inception (2010).mkv",
              "movies/Alien (1979)/Alien (1979).mkv", "movies/Zodiac (2007)/Zodiac (2007).mkv"]:
        fp = os.path.join(ROOT, p)
        os.makedirs(os.path.dirname(fp), exist_ok=True)
        open(fp, "wb").write(b"x")
    yield
    for n in ("ft3movies",):
        requests.delete(URL + "/Library/VirtualFolders", params={"name": n}, headers=hdr(S.get("token")), timeout=30)


def test_startup_wizard_and_auth():
    if not requests.get(URL + "/System/Info/Public", timeout=10).json().get("StartupWizardCompleted"):
        r = requests.post(URL + "/Startup/Configuration", json={"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"}, headers=hdr(), timeout=10)
        assert r.status_code in (200, 204), r.text
        requests.get(URL + "/Startup/User", headers=hdr(), timeout=10)
        r = requests.post(URL + "/Startup/User", json={"Name": "ftadmin", "Password": "ftpass"}, headers=hdr(), timeout=10)
        assert r.status_code in (200, 204), r.text
        r = requests.post(URL + "/Startup/Complete", headers=hdr(), timeout=10)
        assert r.status_code in (200, 204)
    r = requests.post(URL + "/Users/AuthenticateByName", json={"Username": "ftadmin", "Pw": "ftpass"}, headers=hdr(), timeout=20)
    assert r.status_code == 200, r.text
    S["token"] = r.json()["AccessToken"]
    S["uid"] = r.json()["User"]["Id"]
    rec("auth", 200, {"user": r.json()["User"]["Name"], "hasPassword": r.json()["User"]["HasPassword"]})


def test_auth_wrong_password_rejected():
    r = requests.post(URL + "/Users/AuthenticateByName", json={"Username": "ftadmin", "Pw": "wrong"}, headers=hdr(), timeout=20)
    rec("auth_wrong", r.status_code, "")
    assert r.status_code == 401


def _login(pw):
    return requests.post(URL + "/Users/AuthenticateByName", json={"Username": "ftadmin", "Pw": pw}, headers=hdr(), timeout=20)


def test_password_change_rehash_and_login():
    try:
        r = api("POST", f"/Users/Password?userId={S['uid']}", json={"CurrentPw": "ftpass", "NewPw": "ftpass2"})
        assert r.status_code in (200, 204), r.text
        assert _login("ftpass2").status_code == 200
        assert _login("ftpass").status_code == 401
    finally:
        # restore the original password, then re-login (token may be revoked by change)
        for cur in ("ftpass2", "ftpass"):
            lr = _login(cur)
            if lr.status_code == 200:
                if cur != "ftpass":
                    requests.post(URL + f"/Users/Password?userId={S['uid']}", json={"CurrentPw": cur, "NewPw": "ftpass"},
                                  headers=hdr(lr.json()["AccessToken"]), timeout=20)
                break
        lr = _login("ftpass")
        assert lr.status_code == 200
        S["token"] = lr.json()["AccessToken"]
    rec("pw_change", 200, "ok")


def test_localization_cultures():
    r = api("GET", "/Localization/Cultures")
    assert r.status_code == 200
    b = r.json()
    rec("cultures", 200, sorted(c["TwoLetterISOLanguageName"] for c in b))
    assert len(b) > 100 and any(c["TwoLetterISOLanguageName"] == "en" for c in b)


def test_localization_countries():
    r = api("GET", "/Localization/Countries")
    assert r.status_code == 200
    b = r.json()
    rec("countries", 200, sorted(c["TwoLetterISORegionName"] for c in b))
    assert len(b) > 100 and any(c["TwoLetterISORegionName"] == "US" for c in b)


def test_localization_parental_ratings():
    r = api("GET", "/Localization/ParentalRatings")
    assert r.status_code == 200
    b = r.json()
    rec("ratings", 200, sorted((c["Name"], c.get("Value")) for c in b))
    assert len(b) > 5 and any(c["Name"] == "PG-13" for c in b)


def test_localization_options():
    r = api("GET", "/Localization/Options")
    assert r.status_code == 200
    b = r.json()
    rec("options", 200, sorted((c["Name"], c["Value"]) for c in b))
    assert len(b) > 10 and any(c["Value"] == "en-US" for c in b)


def test_branding_configuration():
    r = requests.get(URL + "/Branding/Configuration", timeout=10)
    assert r.status_code == 200
    rec("branding", 200, r.json())
    for k in ("SplashscreenEnabled",):
        assert k in r.json()


def test_branding_css():
    r = requests.get(URL + "/Branding/Css", timeout=10)
    rec("branding_css", r.status_code, r.text)
    assert r.status_code in (200, 204)


def test_xml_config_serialization_roundtrip():
    r = api("GET", "/System/Configuration")
    assert r.status_code == 200
    c = r.json()
    old = c["ServerName"] if "ServerName" in c else None
    c["MetadataPath"] = c.get("MetadataPath", "")
    r = api("POST", "/System/Configuration", data=json.dumps(c))
    assert r.status_code in (200, 204), r.text
    r = api("GET", "/System/Configuration")
    assert r.status_code == 200
    rec("config", 200, {k: r.json()[k] for k in ("UICulture", "MetadataCountryCode", "EnableMetrics") if k in r.json()})
    # branding config is XML-serialized
    b = api("GET", "/System/Configuration/branding").json()
    b2 = dict(b, CustomCss="body{color:red}")
    r = api("POST", "/System/Configuration/branding", data=json.dumps(b2))
    assert r.status_code in (200, 204), r.text
    r = requests.get(URL + "/Branding/Configuration", timeout=10)
    assert r.json()["CustomCss"] == "body{color:red}"
    api("POST", "/System/Configuration/branding", data=json.dumps(b))
    rec("branding_roundtrip", 200, "ok")


def test_library_scan():
    r = api("POST", "/Library/VirtualFolders", params={"name": "ft3movies", "collectionType": "movies", "refreshLibrary": "false"},
            json={"LibraryOptions": {"PathInfos": [{"Path": f"{ROOT}/movies"}], "EnableRealtimeMonitor": False,
                                     "MetadataSavers": [], "TypeOptions": [], "EnableInternetProviders": False}})
    assert r.status_code in (200, 204), r.text
    api("POST", "/Library/Refresh")
    deadline = time.time() + 120
    while time.time() < deadline:
        time.sleep(3)
        if len(items(IncludeItemTypes="Movie")) >= 4:
            time.sleep(5)
            break
    assert len(items(IncludeItemTypes="Movie")) >= 4


@pytest.mark.parametrize("order,key", [("Ascending", "asc"), ("Descending", "desc")])
def test_sort_by_sortname(order, key):
    names = [i["Name"] for i in items(IncludeItemTypes="Movie", SortBy="SortName", SortOrder=order)]
    rec("sort_" + key, 200, names)
    exp = sorted(names, key=str.lower, reverse=(order == "Descending"))
    assert names == exp, names


def test_sort_by_production_year_and_random():
    ys = [i.get("ProductionYear") for i in items(IncludeItemTypes="Movie", SortBy="ProductionYear")]
    rec("sort_year", 200, ys)
    assert ys == sorted(ys)
    rnd = items(IncludeItemTypes="Movie", SortBy="Random")
    assert len(rnd) == 4
    r = api("GET", "/Items", params={"Recursive": "true", "IncludeItemTypes": "Movie", "SortBy": "DateCreated,SortName"})
    assert r.status_code == 200


def test_playlist_lifecycle():
    ids = [i["Id"] for i in items(IncludeItemTypes="Movie", SortBy="SortName")]
    r = api("POST", "/Playlists", data=json.dumps({"Name": "ft3pl", "Ids": ids[:2], "UserId": S["uid"], "MediaType": "Video"}))
    assert r.status_code == 200, r.text
    pid = r.json()["Id"]
    try:
        r = api("GET", f"/Playlists/{pid}/Items", params={"userId": S["uid"]})
        assert r.status_code == 200
        assert r.json()["TotalRecordCount"] == 2
        rec("playlist_items", 200, [i["Name"] for i in r.json()["Items"]])
        r = api("POST", f"/Playlists/{pid}/Items", params={"ids": ids[2], "userId": S["uid"]})
        assert r.status_code in (200, 204), r.text
        r = api("GET", f"/Playlists/{pid}/Items", params={"userId": S["uid"]})
        assert r.json()["TotalRecordCount"] == 3
        entry = r.json()["Items"][0]["PlaylistItemId"]
        r = api("DELETE", f"/Playlists/{pid}/Items", params={"entryIds": entry})
        assert r.status_code in (200, 204), r.text
        r = api("GET", f"/Playlists/{pid}/Items", params={"userId": S["uid"]})
        assert r.json()["TotalRecordCount"] == 2
        assert [p["Name"] for p in items(IncludeItemTypes="Playlist")] == ["ft3pl"]
        rec("playlist_final", 200, [i["Name"] for i in r.json()["Items"]])
    finally:
        api("DELETE", f"/Items/{pid}")


def test_playlist_not_found():
    r = api("GET", "/Playlists/00000000000000000000000000000001/Items", params={"userId": S["uid"]})
    rec("playlist_404", r.status_code, "")
    assert r.status_code == 404


def test_collection_lifecycle():
    ids = [i["Id"] for i in items(IncludeItemTypes="Movie", SortBy="SortName")]
    r = api("POST", "/Collections", params={"name": "ft3col", "ids": ",".join(ids[:2])})
    assert r.status_code == 200, r.text
    cid = r.json()["Id"]
    try:
        cols = items(IncludeItemTypes="BoxSet")
        assert [c["Name"] for c in cols] == ["ft3col"]
        r = api("GET", "/Items", params={"ParentId": cid, "userId": S["uid"]})
        assert r.json()["TotalRecordCount"] == 2
        r = api("POST", f"/Collections/{cid}/Items", params={"ids": ids[2]})
        assert r.status_code in (200, 204), r.text
        assert api("GET", "/Items", params={"ParentId": cid}).json()["TotalRecordCount"] == 3
        r = api("DELETE", f"/Collections/{cid}/Items", params={"ids": ids[2]})
        assert r.status_code in (200, 204), r.text
        n = api("GET", "/Items", params={"ParentId": cid}).json()
        assert n["TotalRecordCount"] == 2
        rec("collection_final", 200, sorted(i["Name"] for i in n["Items"]))
    finally:
        api("DELETE", f"/Items/{cid}")


def test_unauthenticated_localization_rejected():
    r = requests.get(URL + "/Localization/Cultures", timeout=10)
    rec("loc_unauth", r.status_code, "")
    assert r.status_code in (401, 403)
