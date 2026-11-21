import os
from pathlib import Path
import unittest
from unittest.mock import MagicMock, patch

import paramiko
import ssh_remote


class SshRemoteTests(unittest.TestCase):
    def settings(self):
        return {"hostname": "192.0.2.10", "port": "2222", "user": "deploy",
                "identityfile": ["/keys/private"], "userknownhostsfile": "/keys/pinned"}

    @patch("ssh_remote.paramiko.SSHClient")
    @patch("ssh_remote.paramiko.SSHConfig.from_path")
    def test_connect_requires_pinned_key_without_password_agent_or_discovery(self, config, factory):
        config.return_value.lookup.return_value = self.settings()
        with patch.dict(os.environ, {}, clear=True):
            client = ssh_remote.connect(config_path="/config")
        client.load_host_keys.assert_called_once_with(str(Path("/keys/pinned")))
        self.assertIsInstance(client.set_missing_host_key_policy.call_args.args[0], paramiko.RejectPolicy)
        kwargs = client.connect.call_args.kwargs
        self.assertEqual(kwargs["key_filename"], str(Path("/keys/private")))
        self.assertFalse(kwargs["allow_agent"])
        self.assertFalse(kwargs["look_for_keys"])
        self.assertNotIn("password", kwargs)

    @patch("ssh_remote.paramiko.SSHClient")
    @patch("ssh_remote.paramiko.SSHConfig.from_path")
    def test_untrusted_host_is_rejected_without_retry_or_saved_key(self, config, factory):
        config.return_value.lookup.return_value = self.settings()
        client = factory.return_value
        client.connect.side_effect = paramiko.SSHException("untrusted host")
        with self.assertRaises(paramiko.SSHException):
            ssh_remote.connect(config_path="/config")
        client.connect.assert_called_once()
        client.close.assert_called_once()
        client.save_host_keys.assert_not_called()

    @patch("ssh_remote.paramiko.SSHClient")
    @patch("ssh_remote.paramiko.SSHConfig.from_path")
    def test_ambiguous_or_unpinned_configuration_never_connects(self, config, factory):
        for field, value in (("identityfile", []), ("identityfile", ["one", "two"]),
                             ("userknownhostsfile", ""), ("proxycommand", "anything")):
            settings = self.settings()
            settings[field] = value
            config.return_value.lookup.return_value = settings
            with self.assertRaises(ValueError):
                ssh_remote.connect(config_path="/config")
        factory.assert_not_called()

    def test_sudo_credential_is_separate_and_redacted(self):
        client = MagicMock()
        secret = "unit-test-secret"
        stdin, stdout, stderr = MagicMock(), MagicMock(), MagicMock()
        stdout.read.return_value = secret.encode()
        stderr.read.return_value = secret.encode()
        stdout.channel.recv_exit_status.return_value = 0
        client.exec_command.return_value = stdin, stdout, stderr
        with patch.dict(os.environ, {}, clear=True), self.assertRaises(ValueError):
            ssh_remote.run(client, "true", sudo=True)
        client.exec_command.assert_not_called()
        with patch.dict(os.environ, {"DSM_CODEX_PASSWORD": secret}):
            self.assertEqual(ssh_remote.run(client, "true", sudo=True),
                             (0, "[redacted]", "[redacted]"))
        self.assertNotIn(secret, client.exec_command.call_args.args[0])
        stdin.write.assert_called_once_with(secret + "\n")


if __name__ == "__main__":
    unittest.main()
