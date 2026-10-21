import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import game


class FakeCompletedProcess:
    def __init__(self, returncode=0, stderr='', stdout=''):
        self.returncode = returncode
        self.stderr = stderr
        self.stdout = stdout


class DeployTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.game_dir = self.root / 'game'
        self.game_dir.mkdir()
        (self.root / 'src').mkdir()
        (self.root / 'thirdparty' / 'TLDLoaderMods').mkdir(parents=True)

        self.plugin = self.root / 'src/TLDRevamp/bin/Release/netstandard2.1/TLDRevamp.dll'
        self.preloader = self.root / 'src/TLDRevamp.Preloader/bin/Release/netstandard2.1/TLDRevamp.Preloader.dll'
        self.mp_api = self.root / 'src/TLDRevamp.Multiplayer/bin/Release/netstandard2.1/TLDRevamp.Multiplayer.dll'
        self.approved = self.root / 'packaging/approved-mods.txt'
        self.shim = self.root / 'src/TLDLoaderShim/bin/Release/netstandard2.1/TLDLoader.dll'
        for path, data in (
            (self.plugin, b'built-main'),
            (self.preloader, b'built-preloader'),
            (self.mp_api, b'built-mp-api'),
            (self.approved, b'approved-mods'),
            (self.shim, b'built-shim'),
        ):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)

        self.run = mock.Mock(return_value=FakeCompletedProcess())
        self.sh = mock.Mock(return_value=FakeCompletedProcess())
        self.identity = mock.Mock(return_value=None)
        patchers = [
            mock.patch.object(game, 'ROOT', self.root),
            mock.patch.object(game.tldenv, 'GAME_DIR', self.game_dir),
            mock.patch.object(game, 'PRELOADER_DLL', self.preloader),
            mock.patch.object(game, 'MP_API_DLL', self.mp_api),
            mock.patch.object(game, 'APPROVED_MODS', self.approved),
            mock.patch.object(game, 'plugin_dll', return_value=self.plugin),
            mock.patch.object(game, 'require_test_identity', self.identity),
            mock.patch.object(game, 'running', return_value=False),
            mock.patch.object(game, 'local_mods'),
            mock.patch.object(game.subprocess, 'run', self.run),
            mock.patch.object(game, 'sh', self.sh),
            mock.patch.object(game, 'ssh_base', return_value=['ssh', '-i', 'test-key']),
            mock.patch.object(game, 'REMOTE', 'tester@test-host'),
            mock.patch.object(game, 'RGAME', 'copy/game'),
            mock.patch.object(game.tldenv, 'rsync_path', side_effect=lambda path: path),
            mock.patch.dict(os.environ, {'TLD_TEST_MODS': ''}, clear=False),
        ]
        for patcher in patchers:
            patcher.start()
            self.addCleanup(patcher.stop)

    def test_deploy_local_copies_exact_preloader_to_patcher_and_preserves_main_copy(self):
        plugin_dst = self.game_dir / 'BepInEx/plugins/TLDRevamp'
        plugin_dst.mkdir(parents=True)
        (plugin_dst / 'TLDRevamp.dll').write_bytes(b'existing-main-fixture')

        game.deploy_local()

        self.assertEqual((plugin_dst / 'TLDRevamp.dll').read_bytes(), b'built-main')
        self.assertEqual(
            (self.game_dir / 'BepInEx/patchers/TLDRevamp/TLDRevamp.Preloader.dll').read_bytes(),
            b'built-preloader',
        )
        self.assertEqual((plugin_dst / 'TLDRevamp.Multiplayer.dll').read_bytes(), b'built-mp-api')
        self.run.assert_not_called()

    def test_deploy_local_missing_preloader_fails_before_copying_any_bytes(self):
        plugin_dst = self.game_dir / 'BepInEx/plugins/TLDRevamp'
        plugin_dst.mkdir(parents=True)
        existing = plugin_dst / 'TLDRevamp.dll'
        existing.write_bytes(b'existing-main-fixture')
        self.preloader.unlink()

        with self.assertRaisesRegex(RuntimeError, 'built preloader missing'):
            game.deploy_local()

        self.assertEqual(existing.read_bytes(), b'existing-main-fixture')
        self.assertFalse((self.game_dir / 'BepInEx/patchers/TLDRevamp').exists())

    def test_deploy_remote_uses_exact_built_preloader_and_separate_patcher_destination(self):
        game.deploy()

        self.assertEqual(self.run.call_count, 2)
        main_args = self.run.call_args_list[0].args[0]
        patcher_args = self.run.call_args_list[1].args[0]
        self.assertIn(str(self.plugin), main_args)
        self.assertNotIn(str(self.preloader), main_args)
        self.assertIn(str(self.preloader), patcher_args)
        self.assertIn('--mkpath', patcher_args)
        self.assertIn('tester@test-host:copy/game/BepInEx/patchers/TLDRevamp/', patcher_args)
        self.assertNotIn('tester@test-host:copy/game/BepInEx/plugins/TLDRevamp/', patcher_args[-1])

    def test_deploy_remote_missing_preloader_makes_no_subprocess_call(self):
        self.preloader.unlink()

        with self.assertRaisesRegex(RuntimeError, 'built preloader missing'):
            game.deploy()

        self.run.assert_not_called()
        self.sh.assert_not_called()

    def test_deploy_remote_failed_patcher_rsync_surfaces_system_exit(self):
        self.run.side_effect = [
            FakeCompletedProcess(),
            FakeCompletedProcess(returncode=23, stderr='patcher failed'),
        ]

        with self.assertRaises(SystemExit) as raised:
            game.deploy()

        self.assertIn('deploying preloader', str(raised.exception))
        self.assertIn('patcher failed', str(raised.exception))

    def test_identity_is_checked_before_remote_mutation(self):
        self.identity.side_effect = RuntimeError('refused identity')

        with self.assertRaisesRegex(RuntimeError, 'refused identity'):
            game.deploy()

        self.run.assert_not_called()
        self.sh.assert_not_called()

    def test_identity_is_checked_before_local_mutation(self):
        self.identity.side_effect = RuntimeError('refused identity')

        with self.assertRaisesRegex(RuntimeError, 'refused identity'):
            game.deploy_local()

        self.assertFalse((self.game_dir / 'BepInEx/plugins/TLDRevamp').exists())
        self.assertFalse((self.game_dir / 'BepInEx/patchers/TLDRevamp').exists())
        self.run.assert_not_called()

    def test_remote_selected_mod_is_synced_and_unselected_examples_are_removed(self):
        os.environ['TLD_TEST_MODS'] = 'CarTrailer'
        mod = self.root / 'src/Mods/CarTrailer/bin/Release/netstandard2.1/CarTrailer.dll'
        mod.parent.mkdir(parents=True)
        mod.write_bytes(b'car-trailer')

        game.deploy()

        self.assertEqual(self.run.call_count, 3)
        mod_args = self.run.call_args_list[2].args[0]
        self.assertIn(str(mod), mod_args)
        self.assertIn('tester@test-host:copy/game/BepInEx/plugins/CarTrailer/', mod_args)
        shell_commands = [call.args[0] for call in self.sh.call_args_list]
        for name in ('HeadUnit', 'ApiProbe', 'TrailerHitches'):
            self.assertTrue(any('rm -rf' in cmd and name in cmd for cmd in shell_commands), name)
        self.assertFalse(any('rm -rf' in cmd and 'CarTrailer' in cmd for cmd in shell_commands))


if __name__ == '__main__':
    unittest.main()
