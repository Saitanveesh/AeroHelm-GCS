"""Static checks for private-only Oracle deployment, no actual deployment."""
from pathlib import Path

CLOUD = Path(__file__).resolve().parents[2]


def test_compose_has_least_privilege_and_loopback_only_ingress():
    base = (CLOUD / "compose.yaml").read_text()
    ingress = (CLOUD / "compose.oci.yaml").read_text()
    assert 'POSTGRES_USER: postgres' in base
    assert 'db_app_password' in base
    assert 'migrator_database_url' in base
    assert 'profiles: ["migrate"]' in base
    assert 'no-new-privileges:true' in base
    assert 'internal: true' in base
    assert '127.0.0.1:' in ingress
    assert '0.0.0.0:' not in ingress
    assert 'cloud.app.compute_worker' in base
    assert 'cloud.app.scheduler' in base


def test_bootstrap_cannot_overwrite_existing_credential(tmp_path, monkeypatch):
    from cloud.deploy.oci import bootstrap_secrets

    root = tmp_path / "cloud-secrets"
    root.mkdir()
    (root / "db_app_password.txt").write_text("existing-secret-not-to-clobber")
    monkeypatch.setattr(bootstrap_secrets, "SECRET_DIR", root)
    import pytest
    with pytest.raises(SystemExit):
        bootstrap_secrets.generate()
    assert (root / "db_app_password.txt").read_text() == "existing-secret-not-to-clobber"
