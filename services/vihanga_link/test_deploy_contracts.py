"""Offline deployment-contract tests: no VM changes or physical UART."""
from __future__ import annotations

from pathlib import Path
import stat

from services.vihanga_link.deploy import configure_relay
from services.vihanga_link.deploy import configure_pi

ROOT = Path(__file__).resolve().parent
DEPLOY = ROOT / "deploy"


def test_parallel_service_does_not_replace_legacy_or_publish_raw_mavlink():
    oracle = (DEPLOY / "vihanga-relay.service").read_text()
    pi = (DEPLOY / "vihanga-pi.service").read_text()
    vm_install = (DEPLOY / "install_oracle.sh").read_text()
    pi_install = (DEPLOY / "install_pi.sh").read_text()
    runbook = (ROOT.parent.parent / "docs" /
               "VIHANGA_ORACLE_PI_LIVE_LINK_DEPLOY.md").read_text()
    assert "--host 127.0.0.1 --port 8765 --workers 1" in oracle
    assert "EnvironmentFile=/etc/vihanga/relay.env" in oracle
    assert "EnvironmentFile=/etc/vihanga/pi.env" in pi
    assert "User=vihanga" in oracle and "User=vihanga" in pi
    assert "PrivateTmp=no" in pi  # shared /tmp UART lock
    assert "Restart=on-failure" in oracle and "Restart=on-failure" in pi
    for forbidden in ("systemctl stop aerohelm-api",
                      "systemctl restart aerohelm-api",
                      "systemctl disable aerohelm-api"):
        assert forbidden not in vm_install
    assert "tailscale serve reset" not in vm_install
    assert "systemctl stop" not in pi_install
    assert "--https=8443 http://127.0.0.1:8765" in runbook
    assert "aerohelm-api.service" in runbook


def test_relay_secrets_not_overwritten_or_echoed(monkeypatch, tmp_path, capsys):
    original_dest = configure_relay.DEST
    dest = tmp_path / "private" / "relay.env"
    monkeypatch.setattr(configure_relay, "DEST", dest)
    monkeypatch.setattr(configure_relay.os, "geteuid", lambda: 0)
    ocid = "ocid1.instance.oc1.iad." + "a" * 48
    monkeypatch.setattr(configure_relay, "metadata_id", lambda: ocid)
    answers = iter(["aerohelm-vcn.tailf3ab6a.ts.net", "AH-HW-001",
                    "1", "f" * 64, "operator"])
    monkeypatch.setattr("builtins.input", lambda _: next(answers))
    monkeypatch.setattr(configure_relay, "getpass",
                        lambda _: "safe-local-passphrase-only")
    try:
        configure_relay.main()
        data = dest.read_text()
        assert (dest.stat().st_mode & 0o777) == stat.S_IRUSR | stat.S_IWUSR
        assert "VIHANGA_ORACLE_INSTANCE_OCID=" + ocid in data
        assert "VIHANGA_ALLOW_DISARMED_STREAM_REQUEST=0" in data
        assert "VIHANGA_DEVICE_TOKEN_SHA256=" + "f" * 64 in data
        assert "safe-local-passphrase-only" not in data
        assert "$argon2id$" in data
        assert "VIHANGA_JWT_KEY=" in data
        printed = capsys.readouterr().out
        assert "safe-local-passphrase-only" not in printed
        assert ocid not in printed
        assert "f" * 64 not in printed
        with __import__("pytest").raises(SystemExit, match="Refusing to overwrite"):
            configure_relay.main()
    finally:
        monkeypatch.setattr(configure_relay, "DEST", original_dest)


def test_pi_device_token_is_separated_from_root_only_systemd_environment():
    assert str(configure_pi.ENV) == "/etc/vihanga/pi.env"
    assert str(configure_pi.TOKEN) == "/var/lib/vihanga/device-token"
    source = (DEPLOY / "configure_pi.py").read_text()
    assert "secrets.token_hex(48)" in source
    assert "os.chown(TOKEN, user.pw_uid, user.pw_gid)" in source
    assert "os.O_EXCL, 0o600" in source
    assert "VIHANGA_DISARMED_STREAM_PROBE" in source
