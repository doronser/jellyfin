import json, os, shutil, time, pytest, requests

URL = os.environ.get("JELLYFIN_URL", "http://localhost:8096")
SIDE = os.environ.get("FT_SIDE", "target")
ROOT = "/tmp/ftmedia4"
AUTHV = 'Client="ft", Device="ft", DeviceId="ft-dev4", Version="1.0"'
S = {}
OUT = f"/tmp/ft4_{SIDE}.json"
LIB = "ft4movies"


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
    p.setdefault("Recursive", "true")
    r = api("GET", "/Items", params=p)
    assert r.status_code == 200, r.text
    return r.json()["Items"]


def lib_opts(path):
    return {"LibraryOptions": {"PathInfos": [{"Path": path}], "EnableRealtimeMonitor": False,
                               "MetadataSavers": [], "TypeOptions": [], "EnableInternetProviders": False}}


@pytest.fixture(scope="module", autouse=True)
def setup():
    if os.path.exists(OUT):
        os.remove(OUT)
    end = time.time() + 180
    while time.time() < end and requests.get(URL + "/health", timeout=10).status_code != 200:
        time.sleep(2)
    assert requests.get(URL + "/health", timeout=10).status_code == 200
    deadline = time.time() + 180
    while time.time() < deadline:
        if requests.get(URL + "/Startup/Configuration", headers=hdr(), timeout=10).status_code != 503:
            break
        time.sleep(2)
    shutil.rmtree(ROOT, ignore_errors=True)
    files = ["movies/The Matrix (1999)/The Matrix (1999).mkv", "movies/Inception (2010)/Inception (2010).mkv",
             "movies/Alien (1979)/Alien (1979).mkv",
             "shows/Breaking Bad (2008)/Season 01/Breaking Bad S01E01.mkv",
             "shows/Breaking Bad (2008)/Season 01/Breaking Bad S01E02.mkv",
             "shows/Breaking Bad (2008)/Season 02/Breaking Bad S02E01.mkv",
             "music/Pink Floyd/The Wall/01 - In the Flesh.mp3",
             "music/Pink Floyd/The Wall/02 - The Thin Ice.mp3",
             "music/Queen/Jazz/01 - Mustapha.mp3",
             "extra/Zodiac (2007)/Zodiac (2007).mkv"]
    for p in files:
        fp = os.path.join(ROOT, p)
        os.makedirs(os.path.dirname(fp), exist_ok=True)
        open(fp, "wb").write(b"x")
    yield
    for n in (LIB, LIB + "tv", LIB + "music"):
        requests.delete(URL + "/Library/VirtualFolders", params={"name": n, "refreshLibrary": "false"}, headers=hdr(S.get("token")), timeout=30)


def wait_items(n, **p):
    deadline = time.time() + 180
    while time.time() < deadline:
        time.sleep(3)
        if len(items(**p)) >= n:
            time.sleep(4)
            return
    pytest.fail("scan did not produce items")


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
    rec("auth", 200, {"user": r.json()["User"]["Name"]})


def test_unauthenticated_items_rejected():
    r = requests.get(URL + "/Items", timeout=10)
    rec("items_unauth", r.status_code, "")
    assert r.status_code in (401, 403)


def test_virtual_folder_add_list():
    r = api("POST", "/Library/VirtualFolders", params={"name": LIB, "collectionType": "movies", "refreshLibrary": "false"}, json=lib_opts(f"{ROOT}/movies"))
    rec("vf_add", r.status_code, "")
    assert r.status_code in (200, 204), r.text
    r = api("GET", "/Library/VirtualFolders")
    assert r.status_code == 200
    f = [x for x in r.json() if x["Name"] == LIB]
    assert len(f) == 1 and f[0]["CollectionType"] == "movies" and f[0]["Locations"] == [f"{ROOT}/movies"]
    rec("vf_list", 200, {"CollectionType": f[0]["CollectionType"], "Locations": f[0]["Locations"]})


def test_virtual_folder_duplicate_conflict():
    r = api("POST", "/Library/VirtualFolders", params={"name": LIB, "collectionType": "movies", "refreshLibrary": "false"}, json=lib_opts(f"{ROOT}/movies"))
    rec("vf_dup", r.status_code, "")
    # baseline behaviour: duplicate name is accepted and de-duplicated with a numeric suffix
    assert r.status_code in (204, 409), r.text
    names = sorted(x["Name"] for x in api("GET", "/Library/VirtualFolders").json())
    rec("vf_dup_names", 200, names)
    for n in names:
        if n != LIB:
            api("DELETE", "/Library/VirtualFolders", params={"name": n, "refreshLibrary": "false"})
    assert [x["Name"] for x in api("GET", "/Library/VirtualFolders").json()] == [LIB]


def test_virtual_folder_add_missing_path_rejected():
    r = api("POST", "/Library/VirtualFolders", params={"name": "ft4bad", "collectionType": "movies", "refreshLibrary": "false"}, json=lib_opts(f"{ROOT}/does-not-exist"))
    rec("vf_badpath", r.status_code, "")
    assert r.status_code in (400, 404), r.text
    names = [x["Name"] for x in api("GET", "/Library/VirtualFolders").json()]
    assert "ft4bad" not in names


def test_media_path_add_remove():
    r = api("POST", "/Library/VirtualFolders/Paths", params={"refreshLibrary": "false"}, json={"Name": LIB, "Path": f"{ROOT}/extra"})
    rec("path_add", r.status_code, "")
    assert r.status_code in (200, 204), r.text
    f = [x for x in api("GET", "/Library/VirtualFolders").json() if x["Name"] == LIB][0]
    assert sorted(f["Locations"]) == sorted([f"{ROOT}/movies", f"{ROOT}/extra"])
    rec("path_list", 200, sorted(f["Locations"]))
    r = api("DELETE", "/Library/VirtualFolders/Paths", params={"name": LIB, "path": f"{ROOT}/extra", "refreshLibrary": "false"})
    rec("path_remove", r.status_code, "")
    assert r.status_code in (200, 204), r.text
    f = [x for x in api("GET", "/Library/VirtualFolders").json() if x["Name"] == LIB][0]
    assert f["Locations"] == [f"{ROOT}/movies"]


def test_media_path_remove_unknown_library_404():
    r = api("DELETE", "/Library/VirtualFolders/Paths", params={"name": "nope-lib", "path": f"{ROOT}/extra"})
    rec("path_remove_404", r.status_code, "")
    assert r.status_code == 404


def test_remove_unknown_virtual_folder_404():
    r = api("DELETE", "/Library/VirtualFolders", params={"name": "nope-lib", "refreshLibrary": "false"})
    rec("vf_remove_404", r.status_code, "")
    assert r.status_code == 404


def test_add_more_libraries_and_scan():
    for name, ctype, sub in ((LIB + "tv", "tvshows", "shows"), (LIB + "music", "music", "music")):
        r = api("POST", "/Library/VirtualFolders", params={"name": name, "collectionType": ctype, "refreshLibrary": "false"}, json=lib_opts(f"{ROOT}/{sub}"))
        assert r.status_code in (200, 204), r.text
    r = api("POST", "/Library/Refresh")
    rec("refresh", r.status_code, "")
    assert r.status_code == 204
    wait_items(3, IncludeItemTypes="Movie")
    wait_items(3, IncludeItemTypes="Episode")
    wait_items(3, IncludeItemTypes="Audio")


def test_scheduled_task_scan_media_library():
    r = api("GET", "/ScheduledTasks")
    assert r.status_code == 200
    t = [x for x in r.json() if x.get("Key") == "RefreshLibrary"]
    assert len(t) == 1
    rec("task", 200, {"Name": t[0]["Name"], "Category": t[0]["Category"], "Key": t[0]["Key"],
                      "Description": t[0]["Description"]})


def test_refresh_task_triggers_scan():
    tid = [x for x in api("GET", "/ScheduledTasks").json() if x["Key"] == "RefreshLibrary"][0]["Id"]
    r = api("POST", f"/ScheduledTasks/Running/{tid}")
    rec("task_run", r.status_code, "")
    assert r.status_code == 204
    deadline = time.time() + 120
    while time.time() < deadline:
        t = api("GET", f"/ScheduledTasks/{tid}").json()
        if t["State"] == "Idle" and t.get("LastExecutionResult"):
            break
        time.sleep(2)
    t = api("GET", f"/ScheduledTasks/{tid}").json()
    rec("task_result", 200, {"State": t["State"], "Status": t["LastExecutionResult"]["Status"]})
    assert t["LastExecutionResult"]["Status"] == "Completed"


def test_movie_items():
    names = [i["Name"] for i in items(IncludeItemTypes="Movie", SortBy="SortName")]
    rec("movies", 200, names)
    assert names == ["Alien", "Inception", "The Matrix"]
    ms = items(IncludeItemTypes="Movie", Fields="ProductionYear,Path")
    rec("movie_years", 200, sorted((m["Name"], m.get("ProductionYear")) for m in ms))


def test_series_season_episode_resolution():
    ser = items(IncludeItemTypes="Series")
    rec("series", 200, [s["Name"] for s in ser])
    assert [s["Name"] for s in ser] == ["Breaking Bad"]
    seasons = items(IncludeItemTypes="Season", SortBy="SortName")
    rec("seasons", 200, [s["Name"] for s in seasons])
    assert len(seasons) == 2
    eps = items(IncludeItemTypes="Episode", Fields="ParentIndexNumber,IndexNumber")
    rec("episodes", 200, sorted((e.get("ParentIndexNumber"), e.get("IndexNumber")) for e in eps))
    assert len(eps) == 3


def test_music_resolution():
    songs = items(IncludeItemTypes="Audio")
    rec("songs", 200, sorted(s["Name"] for s in songs))
    assert len(songs) == 3
    albums = items(IncludeItemTypes="MusicAlbum")
    rec("albums", 200, sorted(a["Name"] for a in albums))
    assert len(albums) == 2


def test_items_counts():
    r = api("GET", "/Items/Counts", params={"userId": S["uid"]})
    assert r.status_code == 200
    b = r.json()
    rec("counts", 200, b)
    assert b["MovieCount"] == 3 and b["SeriesCount"] == 1 and b["EpisodeCount"] == 3 and b["SongCount"] == 3


@pytest.mark.parametrize("path", ["/Genres", "/MusicGenres", "/Artists", "/Artists/AlbumArtists", "/Persons", "/Studios"])
def test_name_item_queries(path):
    r = api("GET", path, params={"userId": S["uid"], "Recursive": "true"})
    assert r.status_code == 200, r.text
    b = r.json()
    rec("q" + path, 200, {"total": b["TotalRecordCount"], "names": sorted(i["Name"] for i in b["Items"])})


def test_items_filters_and_paging():
    r = api("GET", "/Items", params={"Recursive": "true", "IncludeItemTypes": "Movie", "Limit": 2, "StartIndex": 1, "SortBy": "SortName"})
    b = r.json()
    rec("paging", 200, {"total": b["TotalRecordCount"], "names": [i["Name"] for i in b["Items"]]})
    assert b["TotalRecordCount"] == 3 and [i["Name"] for i in b["Items"]] == ["Inception", "The Matrix"]
    r = api("GET", "/Items", params={"Recursive": "true", "SearchTerm": "matrix"})
    assert [i["Name"] for i in r.json()["Items"]] == ["The Matrix"]
    rec("search", 200, [i["Name"] for i in r.json()["Items"]])


def test_user_views():
    r = api("GET", "/UserViews", params={"userId": S["uid"]})
    assert r.status_code == 200, r.text
    v = r.json()["Items"]
    rec("userviews", 200, sorted((x["Name"], x.get("CollectionType")) for x in v))
    assert sorted(x["Name"] for x in v) == [LIB, LIB + "music", LIB + "tv"]
    r = api("GET", "/UserViews/GroupingOptions", params={"userId": S["uid"]})
    assert r.status_code == 200
    rec("grouping", 200, sorted(x["Name"] for x in r.json()))
    r = api("GET", "/Items/Latest", params={"userId": S["uid"], "IncludeItemTypes": "Movie"})
    assert r.status_code == 200
    rec("latest", 200, sorted(x["Name"] for x in r.json()))


def test_user_data_favorite_and_played():
    m = items(IncludeItemTypes="Movie", SortBy="SortName")[0]
    iid = m["Id"]
    r = api("POST", f"/UserFavoriteItems/{iid}", params={"userId": S["uid"]})
    assert r.status_code == 200, r.text
    r = api("POST", f"/UserPlayedItems/{iid}", params={"userId": S["uid"]})
    assert r.status_code == 200, r.text
    fav = items(IncludeItemTypes="Movie", Filters="IsFavorite", userId=S["uid"])
    played = items(IncludeItemTypes="Movie", Filters="IsPlayed", userId=S["uid"])
    rec("userdata", 200, {"fav": [i["Name"] for i in fav], "played": [i["Name"] for i in played]})
    assert [i["Name"] for i in fav] == ["Alien"] and [i["Name"] for i in played] == ["Alien"]
    r = api("POST", f"/UserItems/{iid}/UserData", params={"userId": S["uid"]}, data=json.dumps({"IsFavorite": False, "Played": False}))
    assert r.status_code == 200, r.text
    assert items(IncludeItemTypes="Movie", Filters="IsFavorite", userId=S["uid"]) == []
    r = api("DELETE", f"/UserFavoriteItems/{iid}", params={"userId": S["uid"]})
    assert r.status_code == 200


def test_playback_info_media_sources():
    iid = items(IncludeItemTypes="Movie", SortBy="SortName")[0]["Id"]
    r = api("GET", f"/Items/{iid}/PlaybackInfo", params={"userId": S["uid"]})
    rec("playbackinfo", r.status_code, "")
    assert r.status_code == 200, r.text
    ms = r.json()["MediaSources"]
    assert len(ms) == 1 and ms[0]["Path"].endswith("Alien (1979).mkv")
    rec("playbackinfo_src", 200, {"n": len(ms), "name": os.path.basename(ms[0]["Path"]), "container": ms[0].get("Container")})


def test_instant_mix_music_manager():
    song = items(IncludeItemTypes="Audio")[0]["Id"]
    r = api("GET", f"/Songs/{song}/InstantMix", params={"userId": S["uid"], "limit": 10})
    rec("instantmix", r.status_code, "")
    assert r.status_code == 200, r.text
    rec("instantmix_count", 200, len(r.json()["Items"]))


def test_item_not_found():
    r = api("GET", "/Items/00000000000000000000000000000001/Ancestors")
    rec("item_404", r.status_code, "")
    assert r.status_code == 404


def test_virtual_folder_remove_deletes_items():
    r = api("DELETE", "/Library/VirtualFolders", params={"name": LIB, "refreshLibrary": "false"})
    rec("vf_remove", r.status_code, "")
    assert r.status_code in (200, 204), r.text
    names = [x["Name"] for x in api("GET", "/Library/VirtualFolders").json()]
    assert LIB not in names
    api("POST", "/Library/Refresh")
    deadline = time.time() + 120
    while time.time() < deadline and items(IncludeItemTypes="Movie"):
        time.sleep(3)
    assert items(IncludeItemTypes="Movie") == []
    rec("after_remove_views", 200, sorted(x["Name"] for x in api("GET", "/UserViews", params={"userId": S["uid"]}).json()["Items"]))
