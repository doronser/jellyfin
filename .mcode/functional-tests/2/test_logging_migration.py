import json, os, re, shutil, signal, sqlite3, subprocess, time
import pytest, requests

URL = os.environ.get("JELLYFIN_URL", "http://localhost:8096")
BASE = "/tmp/jellyfin-target"
REPO = os.environ.get("WORKSPACE_DIR", "/l2l/workspace") + "/jellyfin"
DLL = os.environ.get("FT_DLL", REPO + "/Jellyfin.Server/bin/Release/net10.0/jellyfin.dll")
CFG = BASE + "/config"
MIG = "20250420060000_CreateUserLoggingConfigFile"

DEFAULT = '{"Serilog":{"MinimumLevel":{"Default":"Information","Override":{"Microsoft":"Warning","System":"Warning"}},"WriteTo":[{"Name":"Console","Args":{"outputTemplate":"[{Timestamp:HH:mm:ss}] [{Level:u3}] [{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}"}},{"Name":"Async","Args":{"configure":[{"Name":"File","Args":{"path":"%JELLYFIN_LOG_DIR%//log_.log","rollingInterval":"Day","retainedFileCountLimit":3,"rollOnFileSizeLimit":true,"fileSizeLimitBytes":100000000,"outputTemplate":"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{ThreadId}] {SourceContext}: {Message}{NewLine}{Exception}"}}]}}],"Enrich":["FromLogContext","WithThreadId"]}}'


def stop(proc):
    if proc and proc.poll() is None:
        proc.send_signal(signal.SIGINT)
        try:
            proc.wait(30)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()


def run_server(logging_json):
    """Start server on an already-initialised data dir with the migration pending."""
    for f in ("logging.json", "logging.old.json"):
        p = f"{CFG}/{f}"
        if os.path.exists(p):
            os.remove(p)
    if logging_json is not None:
        open(f"{CFG}/logging.json", "w").write(logging_json)
    db = sqlite3.connect(f"{BASE}/data/data/jellyfin.db")
    db.execute("delete from __EFMigrationsHistory where MigrationId=?", (MIG,))
    db.commit()
    db.close()
    logf = f"{BASE}/ft_server.log"
    out = open(logf, "w")
    proc = subprocess.Popen(
        ["dotnet", DLL, "--nowebclient", "--datadir", BASE + "/data", "--configdir", CFG,
         "--cachedir", BASE + "/cache", "--logdir", BASE + "/log"],
        stdout=out, stderr=subprocess.STDOUT, cwd=REPO)
    for _ in range(90):
        try:
            if requests.get(URL + "/health", timeout=3).text == "Healthy":
                break
        except requests.RequestException:
            pass
        if proc.poll() is not None:
            break
        time.sleep(2)
    return proc, logf


@pytest.fixture(scope="module", autouse=True)
def prepared():
    if not os.path.exists(f"{BASE}/data/data/jellyfin.db"):
        pytest.fail("target data dir not initialised by lifecycle run")
    yield


def scenario(content):
    proc, logf = run_server(content)
    try:
        r = requests.get(URL + "/health", timeout=5)
        log = open(logf, errors="replace").read()
        return r, log
    finally:
        stop(proc)


def test_server_starts_and_migration_runs_default_moved():
    r, log = scenario(DEFAULT)
    assert r.status_code == 200 and r.text == "Healthy"
    assert "CreateUserLoggingConfigFile" in log
    assert "Failed" not in log or "Failed to apply" not in log
    assert not os.path.exists(f"{CFG}/logging.json")
    assert open(f"{CFG}/logging.old.json").read() == DEFAULT


def test_modified_config_kept():
    mod = json.loads(DEFAULT)
    mod["Serilog"]["MinimumLevel"]["Default"] = "Debug"
    text = json.dumps(mod)
    r, log = scenario(text)
    assert r.status_code == 200
    assert os.path.exists(f"{CFG}/logging.json")
    assert open(f"{CFG}/logging.json").read() == text
    assert not os.path.exists(f"{CFG}/logging.old.json")



def test_no_logging_json_is_noop():
    r, log = scenario(None)
    assert r.status_code == 200
    assert not os.path.exists(f"{CFG}/logging.json")
    assert not os.path.exists(f"{CFG}/logging.old.json")
