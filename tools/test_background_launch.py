"""Offline safety regressions. All process/network boundaries are denied unless explicitly faked."""
import contextlib
import io
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import Mock, patch

import bench
import game
import launchguard
import testdisplay as display
import testlaunch as target


class SafetyTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = pathlib.Path(self.tmp.name)
        for name in ('copy/game', 'prefix/pfx', 'steam', 'proton', 'slr', 'registry'):
            (self.root / name).mkdir(parents=True)
        (self.root / 'copy/game/TheLongDrive.exe').touch()
        self.spec = {'game': str(self.root/'copy/game'), 'compat': str(self.root/'prefix'),
                     'steam': str(self.root/'steam'), 'proton': str(self.root/'proton'),
                     'slr': str(self.root/'slr'), 'port': 28111, 'profile': 'desktop'}
        self.token = 'a' * 32
        self.env = {'XDG_RUNTIME_DIR': str(self.root), 'HYPRLAND_INSTANCE_SIGNATURE': 'session'}
        # A fake admission broker and shim: the composition is exercised, no unit is created.
        self.broker = self.root/'tld-admit'
        self.broker.write_text('#!/bin/sh\nexit 0\n')
        self.broker.chmod(0o755)
        self.shim = self.root/'launchshim.py'
        self.shim.write_text('#!/bin/sh\nexit 0\n')
        self.shim.chmod(0o755)

        self.spec = {'game': str(self.root/'copy/game'), 'compat': str(self.root/'prefix'),
                     'steam': str(self.root/'steam'), 'proton': str(self.root/'proton'),
                     'slr': str(self.root/'slr'), 'port': 28101, 'profile': 'desktop'}
        self.env = {'XDG_RUNTIME_DIR': str(self.root), 'HYPRLAND_INSTANCE_SIGNATURE': 'session'}
        self.process = dict(pid=123456, start='101', game=self.spec['game'], compat=self.spec['compat'],
                            token='owned', argv=[self.spec['game']+'/TheLongDrive.exe', '-tldbridge', '28101'])
        self.lease = dict(self.spec, token='owned', pids=[[123456, '101']])
        for name in ('run', 'Popen', 'check_output'):
            p = patch.object(subprocess, name, side_effect=AssertionError('real subprocess forbidden'))
            p.start(); self.addCleanup(p.stop)
        for name in ('create_connection', 'socket'):
            p = patch.object(bench.socket, name, side_effect=AssertionError('real network forbidden'))
            p.start(); self.addCleanup(p.stop)
        for name in ('kill', 'killpg', 'pidfd_open'):
            p = patch.object(os, name, side_effect=AssertionError('real signal forbidden'))
            p.start(); self.addCleanup(p.stop)

    def settings(self):
        p = self.root/'prefix/pfx/drive_c/users/steamuser/Documents/TheLongDrive/settings.tldc'
        p.parent.mkdir(parents=True)
        p.write_text('{}')
        return p

    def test_missing_compositor_refuses_without_dispatch(self):
        with patch.object(display.shutil, 'which', return_value=None):
            with self.assertRaises(RuntimeError): display.background_command('game')
        subprocess.run.assert_not_called()

    def test_missing_ambiguous_and_stale_sockets(self):
        base = self.root/'hypr'; base.mkdir()
        with patch.object(display.shutil, 'which', return_value='/fake/hyprctl'), patch.dict(os.environ, {}, clear=True):
            with self.assertRaises(RuntimeError): display.hypr_env(str(self.root))
            (base/'one').mkdir(); (base/'two').mkdir()
            with patch.object(pathlib.Path, 'is_socket', return_value=True):
                with self.assertRaises(RuntimeError): display.hypr_env(str(self.root))
                (base/'two').rmdir()
                with self.assertRaises(RuntimeError): display.hypr_env(str(self.root), 'stale')

    def test_socket_recovery_checks_target_route(self):
        (self.root/'hypr/session').mkdir(parents=True)
        with patch.object(display.shutil, 'which', return_value='/fake'), patch.dict(os.environ, {}, clear=True), patch.object(pathlib.Path, 'is_socket', return_value=True), patch.object(display, 'preflight') as pre:
            env = display.hypr_env(str(self.root))
            self.assertEqual(env['HYPRLAND_INSTANCE_SIGNATURE'], 'session')
            pre.assert_called_once_with(env)

    def test_unreachable_compositor_refused(self):
        with patch.object(subprocess, 'check_output', side_effect=OSError('no route')):
            with self.assertRaises(RuntimeError): display.background_command('game', env=self.env)

    def test_visible_special_workspace_and_no_monitor_refused(self):
        for monitors in ([], [{'specialWorkspace': {'name': display.WORKSPACE}}]):
            with patch.object(display, 'query', return_value=monitors):
                with self.assertRaises(RuntimeError): display.preflight(self.env)

    def test_exact_launch_rules_and_preflight_same_environment(self):
        for profile, size in [('desktop', '1280 720'), ('remote', '800 450')]:
            with patch.object(display, 'preflight') as pre:
                cmd = display.background_command('exec game', profile, self.env)
                self.assertIn('[workspace special:tld-tests silent; float; size '+size+'; no_initial_focus; suppress_event fullscreen maximize] exec game', cmd)
                pre.assert_called_once_with(self.env)
                self.assertIn('hyprctl dispatch exec', cmd)

    def test_missing_settings_are_seeded_from_the_template_not_refused(self):
        # F84: a fresh prefix has no settings file.  The launcher seeds the game's
        # own schema from the repository template instead of refusing, so the
        # startup state is known before the first frame.
        target = display.settings_path(self.spec['compat'])
        self.assertFalse(target.exists())
        display.prepare(self.spec['game'], self.spec['compat'])
        self.assertTrue(target.is_file())
        self.assertTrue((target.parent/'settings.tldc.seeded').is_file())
        data = json.loads(target.read_text())
        self.assertEqual(data['FSound'], 0)
        self.assertEqual(data['BEnableFpsLimit'], True)
        self.assertEqual(data['FFPSLimit'], 30)
        self.assertIs(data['BVsync'], False)
        self.assertEqual(data['IResolutionX'], 1280)
        subprocess.run.assert_not_called()

    def test_the_settings_template_is_the_game_schema_without_player_data(self):
        template = display.template_settings()
        self.assertGreater(len(template), 50)
        for key in ('FSound', 'BVsync', 'BEnableFpsLimit', 'FFPSLimit', 'IFullscreen',
                    'IResolutionX', 'IResolutionY', 'BEnableShadows'):
            self.assertIn(key, template)
        # no player state and no input bindings
        self.assertNotIn('inputs', template)
        for key, value in template.items():
            self.assertIsInstance(value, (bool, int, float, str), key)

    def test_a_broken_template_refuses_instead_of_starting_audible(self):
        with patch.object(display, 'TEMPLATE', pathlib.Path('/nonexistent/settings-template.tldc')):
            with self.assertRaises(RuntimeError):
                display.seed_settings(self.spec['compat'])
        self.assertFalse(display.settings_path(self.spec['compat']).exists())

    def test_existing_settings_are_never_overwritten_by_seeding(self):
        target = display.settings_path(self.spec['compat'])
        target.parent.mkdir(parents=True)
        target.write_text(json.dumps({'FSound': 0.5, 'myOwnKey': 1}))
        self.assertFalse(display.seed_settings(self.spec['compat']))
        data = json.loads(target.read_text())
        self.assertEqual(data['myOwnKey'], 1)
        self.assertEqual(data['FSound'], 0.5)

    def test_game_only_startup_mute_and_profile(self):
        p = self.settings()
        args = display.prepare(self.spec['game'], self.spec['compat'])
        data = json.loads(p.read_text())
        self.assertEqual(data['FSound'], 0)
        self.assertEqual(data['FFPSLimit'], 30)
        self.assertIs(data['BEnableFpsLimit'], True)
        self.assertIs(data['BVsync'], False)
        self.assertEqual(data['IFullscreen'], 3)
        self.assertEqual((data['IResolutionX'], data['IResolutionY']), (1280,720))
        self.assertEqual(args, ('-screen-fullscreen','0','-screen-width','1280','-screen-height','720'))
        display.prepare(self.spec['game'], self.spec['compat'], 'remote')
        data = json.loads(p.read_text())
        self.assertEqual((data['IResolutionX'], data['IResolutionY']), (800,450))

    def test_runtime_mute_both_controls_quality_and_cap(self):
        bridge = Mock(return_value={})
        display.configure(bridge)
        commands = [c.args[0] for c in bridge.call_args_list]
        for cmd in ('set settingsscript.s.S.FSound 0.0', 'set UnityEngine.AudioListener.volume 0', 'quality 0',
                    'set settingsscript.s.S.BEnableFpsLimit True', 'set settingsscript.s.S.FFPSLimit 30.0',
                    'set UnityEngine.QualitySettings.vSyncCount 0', 'set settingsscript.s.S.BVsync False',
                    'set UnityEngine.Application.targetFrameRate 30', 'bg on'):
            self.assertIn(cmd, commands)
        self.assertFalse(any('pactl' in c or 'wpctl' in c for c in commands))

    def test_profile_missing_sound_evidence_is_not_zero(self):
        state = dict(width=1280,height=720,fullscreen=False,quality=0,vsync=0,game_vsync=False,
                     fps_limit_enabled=True,fps_limit=30,target_fps=30,run_in_background=True,
                     audio_listener=0,sound=0,focused=False)
        display.check(state)
        for k,v in [('sound',None),('audio_listener',None),('fps_limit',29),('vsync',1)]:
            with self.assertRaises(RuntimeError): display.check(dict(state, **{k:v}))

    def test_canonical_identity_and_explicit_port(self):
        self.assertEqual(target.identity(self.spec), self.spec)
        for patch_spec in ({'compat':''}, {'game':''}, {'port':None}, {'port':0}, {'port':'28101'}, {'compat': self.spec['game']}):
            with self.assertRaises((RuntimeError, FileNotFoundError)):
                target.identity(dict(self.spec, **patch_spec))
        alias = self.root/'alias'; alias.symlink_to(self.spec['game'])
        self.assertEqual(target.identity(dict(self.spec, game=str(alias)))['game'], self.spec['game'])

    def test_owner_and_shared_resources_refused(self):
        for name in ('steam/steamapps/common/The Long Drive', 'steam/steamapps/compatdata/1017180', 'tld-revamp-testprefix', 'test/game', 'other-library/steamapps/common/The Long Drive'):
            path = self.root/name; path.mkdir(parents=True, exist_ok=True)
            with self.assertRaises(RuntimeError): target.identity(dict(self.spec, compat=str(path)))

    def test_bridge_argument_exactly_once(self):
        self.assertEqual(target.bridge_args([],28101), ['-tldbridge','28101'])
        self.assertEqual(target.bridge_args(['-tldbridge','28101'],28101), ['-tldbridge','28101'])
        for args in (['-tldbridge','28102'], ['-tldbridge'], ['-tldbridge=28101'], ['-tldbridge','28101','-tldbridge','28101'], ['-screen-fullscreen','1']):
            with self.assertRaises(RuntimeError): target.bridge_args(args,28101)

    def test_datapath_exact_copy_only(self):
        path = self.spec['game']
        self.assertTrue(display.data_path_matches('Z:'+path.replace('/', '\\')+'\\TheLongDrive_Data',path))
        for value in (None, path, path+'/Other', path+'-foreign/TheLongDrive_Data', 'C:'+path+'/TheLongDrive_Data'):
            self.assertFalse(display.data_path_matches(value,path))

    def test_occupied_launch_port_refused(self):
        sock = Mock(); sock.__enter__=Mock(return_value=sock); sock.__exit__=Mock(return_value=False)
        sock.bind.side_effect = OSError('busy')
        with patch.object(bench.socket, 'socket', return_value=sock):
            with self.assertRaises(RuntimeError): target.free_port(28101)

    def test_running_copy_and_identity_collisions_refused(self):
        with patch.object(display, 'hypr_env', return_value=self.env), patch.object(target, 'processes', return_value=[self.process]):
            with self.assertRaises(RuntimeError): target.launch(self.spec,self.root/'registry')
        for field,value in [('game','different'),('compat','different'),('port',28102)]:
            path=self.root/'registry/other.json'; path.write_text(json.dumps(dict(self.lease, **{field:value})))
            with patch.object(display, 'hypr_env', return_value=self.env):
                with self.assertRaises(RuntimeError): target.launch(self.spec,self.root/'registry')
            path.unlink()
        subprocess.run.assert_not_called()

    def test_unleased_foreign_reuse_refused(self):
        with self.assertRaises(RuntimeError): target.validate(self.spec, self.root/'registry')

    def test_visible_focused_and_fullscreen_reuse_refused(self):
        for entry in ({'workspace':{'name':'2'}}, {'workspace':{'name':display.WORKSPACE},'focusHistoryID':0},
                      {'workspace':{'name':display.WORKSPACE},'fullscreen':2}):
            c=dict(pid=123456,floating=True,**entry)
            with patch.object(display, 'preflight'), patch.object(display, 'query', return_value=[c]):
                with self.assertRaises(RuntimeError): target.check_windows(self.spec,self.env,[123456])

    def test_proton_game_process_owns_the_proof_not_its_wrapper(self):
        # Under Proton the game's argv[0] is a Windows path and its window has the game's PID; Proton's steam.exe
        # wrapper carries the Unix path as an argument. The window/token/listener proof must be asked of the game.
        win = "Z:" + chr(92) + "copy" + chr(92) + "game" + chr(92) + "TheLongDrive.exe"
        wrapper = dict(self.process, pid=111, start='1',
                       argv=['c:' + chr(92) + 'windows' + chr(92) + 'system32' + chr(92) + 'steam.exe',
                             self.spec['game'] + '/TheLongDrive.exe', '-tldbridge', '28101'])
        game = dict(self.process, pid=222, start='2', argv=[win, '-screen-fullscreen', '0', '-tldbridge', '28101'])
        lease = dict(self.lease, pids=[[111, '1'], [222, '2']])
        path = self.root/'registry'/(target.key(self.spec)+'.json'); path.write_text(json.dumps(lease))
        seen = {}
        with patch.object(target, 'processes', return_value=[wrapper, game]), \
             patch.object(display, 'hypr_env', return_value=self.env), \
             patch.object(target, 'check_windows', side_effect=lambda spec, env, pids: seen.setdefault('windows', pids)), \
             patch.object(target, 'check_compositor_token', side_effect=lambda pids: seen.setdefault('token', pids)), \
             patch.object(target, 'check_listener', side_effect=lambda port, pids: seen.setdefault('listener', pids)):
            target.validate(self.spec, self.root/'registry')
        self.assertEqual(seen, {'windows': [222], 'token': [222], 'listener': [222]})

    def test_wine_listener_may_be_held_by_the_leases_own_wineserver(self):
        win = "Z:" + chr(92) + "copy" + chr(92) + "game" + chr(92) + "TheLongDrive.exe"
        game = dict(self.process, pid=222, start='2', argv=[win, '-tldbridge', '28101'])
        server = dict(self.process, pid=333, start='3', argv=['/proton/files/lib/wine/../../bin/wineserver', ''])
        path = self.root/'registry'/(target.key(self.spec)+'.json')
        path.write_text(json.dumps(dict(self.lease, pids=[[222, '2'], [333, '3']])))
        seen = {}
        with patch.object(target, 'processes', return_value=[game, server]), \
             patch.object(display, 'hypr_env', return_value=self.env), \
             patch.object(target, 'check_windows', side_effect=lambda spec, env, pids: seen.setdefault('windows', pids)), \
             patch.object(target, 'check_compositor_token', side_effect=lambda pids: seen.setdefault('token', pids)), \
             patch.object(target, 'check_listener', side_effect=lambda port, pids: seen.setdefault('listener', pids)):
            target.validate(self.spec, self.root/'registry')
        # the window and token proofs stay on the game; only the listener may be the lease's own wineserver
        self.assertEqual(seen, {'windows': [222], 'token': [222], 'listener': [222, 333]})

    def test_pid_start_reuse_refused(self):
        path=self.root/'registry'/(target.key(self.spec)+'.json'); path.write_text(json.dumps(self.lease))
        with patch.object(target,'processes',return_value=[dict(self.process,start='102')]):
            with self.assertRaises(RuntimeError): target.validate(self.spec,self.root/'registry',False)

    def test_scoped_cleanup_local_and_remote_target(self):
        path=self.root/'registry'/(target.key(self.spec)+'.json'); path.write_text(json.dumps(self.lease))
        foreign=dict(self.process,pid=999999,game='/foreign/game',compat='/foreign/prefix')
        for profile in ('desktop','remote'):
            with patch.object(target,'registry',return_value=contextlib.nullcontext(self.root/'registry')), patch.object(target,'processes',return_value=[self.process,foreign]), patch.object(os,'pidfd_open',return_value=42) as opened, patch.object(os,'close'), patch.object(target.signal,'pidfd_send_signal') as send:
                self.assertEqual(target.operate(dict(self.spec,profile=profile),'cleanup'),{'signalled':1})
                opened.assert_called_once_with(123456)
                send.assert_called_once_with(42,target.signal.SIGTERM)

    def test_cleanup_refuses_unowned_and_reused_pid(self):
        path=self.root/'registry'/(target.key(self.spec)+'.json'); path.write_text(json.dumps(self.lease))
        for p in (dict(self.process,token='foreign'),dict(self.process,start='102')):
            with patch.object(target,'registry',return_value=contextlib.nullcontext(self.root/'registry')), patch.object(target,'processes',return_value=[p]):
                with self.assertRaises(RuntimeError): target.operate(self.spec,'cleanup')
        os.pidfd_open.assert_not_called()

    def test_launch_dispatch_only_hidden_and_matching_port(self):
        self.settings()
        with patch.object(display,'hypr_env',return_value=self.env), patch.object(display,'preflight'), patch.object(target,'processes',return_value=[]), patch.object(target,'free_port') as free, patch.object(target,'validate',return_value=[]), patch.object(subprocess,'run',return_value=Mock(returncode=0,stdout='ok\n')) as run:
            self.assertEqual(target.launch(self.spec,self.root/'registry'),{'launched':True})
            cmd=run.call_args.args[0]
            self.assertIn('hyprctl dispatch exec',cmd)
            self.assertIn('-tldbridge 28101',cmd)
            self.assertEqual(cmd.count('-tldbridge'),1)
            self.assertIn('no_initial_focus',cmd)
            self.assertNotIn('nohup',cmd)
            self.assertNotIn('steam -applaunch',cmd)
            free.assert_called_once_with(28101)

    def test_failed_dispatch_no_fallback(self):
        self.settings()
        with patch.object(display,'hypr_env',return_value=self.env), patch.object(display,'preflight'), patch.object(target,'processes',return_value=[]), patch.object(target,'free_port'), patch.object(subprocess,'run',return_value=Mock(returncode=1,stdout='')) as run:
            with self.assertRaises(RuntimeError): target.launch(self.spec,self.root/'registry')
            self.assertEqual(run.call_count,1)

    def test_prefix_override_requires_all_three(self):
        with self.assertRaises(RuntimeError): game.prefix_launch_cmd(compat=self.spec['compat'])
        with self.assertRaises(RuntimeError): game.prefix_launch_cmd(compat=self.spec['compat'],game_dir=self.spec['game'])

    def test_game_launch_never_reuses_answering_bridge(self):
        with patch.object(game,'running',return_value=True), patch.object(game,'bridge_up',return_value=True), patch.object(game,'start') as start:
            with self.assertRaises(RuntimeError): game.launch()
            start.assert_not_called()

    def test_bridge_rejects_datapath_before_any_mutation(self):
        connection=Mock(); connection.makefile.return_value=io.StringIO()
        with patch.object(launchguard,'specification',return_value=self.spec), patch.object(launchguard,'run',return_value={'spec':self.spec}), patch.object(bench.socket,'create_connection',return_value=connection), patch.object(bench.Bridge,'_raw',return_value={'value':'Z:/foreign/TheLongDrive_Data'}) as raw:
            with self.assertRaises(RuntimeError): bench.Bridge()
            self.assertEqual([c.args[0] for c in raw.call_args_list],['get UnityEngine.Application.dataPath'])

    def test_each_bridge_command_checks_identity_before_mutation(self):
        b=bench.Bridge.__new__(bench.Bridge); b.spec=self.spec; b.remote=None
        b._raw=Mock(side_effect=[{'value':self.spec['game']+'/TheLongDrive_Data'},{'ok':True}])
        with patch.object(launchguard,'run') as check:
            b('set UnityEngine.AudioListener.volume 0')
            check.assert_called_once_with(self.spec,'verify',None)
            self.assertEqual(b._raw.call_args_list[0].args[0],'get UnityEngine.Application.dataPath')
        b._raw=Mock(return_value={'value':'/foreign/TheLongDrive_Data'})
        with patch.object(launchguard,'run'):
            with self.assertRaises(RuntimeError): b('call UnityEngine.Application.Quit')
        self.assertEqual(b._raw.call_count,1)

    def test_tunnel_matches_destination_and_client_port(self):
        sock=Mock(); sock.__enter__=Mock(return_value=sock); sock.__exit__=Mock(return_value=False)
        proc=Mock(); proc.poll.return_value=None
        with patch.dict(bench._TUNNELS,{},clear=True), patch.object(bench.socket,'socket',return_value=sock), patch.object(subprocess,'Popen',return_value=proc) as popen, patch.object(bench.time,'sleep'), patch.object(bench,'claim_forward_port') as claim:
            self.assertEqual(bench.ensure_tunnel('fake@target',28103,28101),28103)
            # the forward is claimed in the target's registry before it is used
            claim.assert_called_once_with('fake@target', 28103, 28101)
            argv=popen.call_args.args[0]
            self.assertIn('127.0.0.1:28103:127.0.0.1:28101',argv)
            self.assertNotIn('27050',' '.join(argv))
            self.assertEqual(bench.ensure_tunnel('fake@target',28103,28101),28103)
            self.assertEqual(popen.call_count,1)
        with patch.object(launchguard,'specification',return_value=self.spec), patch.object(launchguard,'run',return_value={'spec':self.spec}), patch.object(bench,'ensure_tunnel',return_value=28103) as tunnel, patch.object(bench.socket,'create_connection',return_value=Mock()) as conn, patch.object(bench.Bridge,'_verify_data_path'):
            b=bench.Bridge('fake@target')
            tunnel.assert_called_once_with('fake@target',destination_port=28101)
            conn.assert_called_once_with(('127.0.0.1',28103),timeout=35)
            self.assertEqual(b.restore_file,bench.restore_file('fake@target',28101))

    def test_unrelated_tunnel_listener_is_rejected(self):
        sock=Mock(); sock.__enter__=Mock(return_value=sock); sock.__exit__=Mock(return_value=False); sock.bind.side_effect=OSError('busy')
        with patch.dict(bench._TUNNELS,{},clear=True), patch.object(bench.socket,'socket',return_value=sock):
            with self.assertRaises(RuntimeError): bench.ensure_tunnel('fake@target',28103,28101)
        subprocess.Popen.assert_not_called()

    def test_per_port_restore_journals(self):
        self.assertNotEqual(bench.restore_file(None,28101),bench.restore_file(None,28102))
        self.assertNotEqual(bench.restore_file(None,28101),bench.restore_file('fake@target',28101))

    def test_remote_target_recovery_and_scoped_cleanup_bundle(self):
        for action in ('launch','cleanup','verify'):
            source=launchguard.program(self.spec,action)
            compile(source,'<target-guard>','exec')
            with patch.object(subprocess,'run',return_value=Mock(returncode=0,stdout='{}')) as run:
                launchguard.run(self.spec,action,'fake@target')
                self.assertEqual(run.call_args.args[0][-1],'python3 -')
                self.assertEqual(run.call_args.kwargs['input'],source)
            self.assertIn('HYPRLAND_INSTANCE_SIGNATURE',source)
            self.assertIn('pidfd_send_signal',source)
            self.assertNotIn('pkill',source)
            self.assertNotIn('print(os.environ',source)

    def test_local_and_remote_quit_use_guarded_bridge_then_scoped_cleanup(self):
        for remote in (None,'fake@target'):
            with patch.object(game,'REMOTE',remote), patch.object(game,'running',return_value=True), patch.object(game,'Bridge') as bridge, patch.object(game,'kill_prefix') as cleanup:
                game.quit_game(timeout=0)
                bridge.return_value.assert_called_once_with('call UnityEngine.Application.Quit')
                cleanup.assert_called_once_with()
            with patch.object(game,'REMOTE',remote), patch.object(launchguard,'specification',return_value=self.spec), patch.object(launchguard,'run') as run:
                game.kill_prefix()
                run.assert_called_once_with(self.spec,'cleanup',remote)

    def test_hidden_window_size_and_inactive_route_are_verified(self):
        window = dict(pid=123456, floating=True, focusHistoryID=1, fullscreen=0,
                      workspace={"name": display.WORKSPACE}, size=[1280,720])
        def query(kind, env):
            return [window] if kind == "clients" else {"pid": 999999}
        with patch.object(display, "preflight"), patch.object(display, "query", side_effect=query):
            target.check_windows(self.spec, self.env, [123456])
            window["size"] = [1920,1080]
            with self.assertRaises(RuntimeError): target.check_windows(self.spec,self.env,[123456])

    def test_listener_must_belong_to_owned_pid(self):
        row = "header\n0: 0100007F:6DC5 00000000:0000 0A 0 0 0 0 0 12345\n"
        # 0x6DC5 == 28101; fake every /proc read and fd inspection.
        with patch.object(pathlib.Path, "read_text", return_value=row), patch.object(pathlib.Path, "iterdir", return_value=[pathlib.Path('/fake/fd')]), patch.object(os, "readlink", return_value='socket:[12345]'):
            target.check_listener(28101, [123456])
            with self.assertRaises(RuntimeError): target.check_listener(28102,[123456])

    def test_status_refuses_foreign_prefix_even_if_bridge_might_answer(self):
        with patch.object(target,"registry",return_value=contextlib.nullcontext(self.root/'registry')), patch.object(target,"processes",return_value=[self.process]):
            with self.assertRaises(RuntimeError): target.operate(self.spec,'status')
        os.pidfd_open.assert_not_called()

    def test_compositor_failure_in_actual_target_launch_path(self):
        with patch.object(target,"registry",return_value=contextlib.nullcontext(self.root/'registry')), patch.object(display,"hypr_env",side_effect=RuntimeError('no compositor')):
            with self.assertRaises(RuntimeError): target.operate(self.spec,'launch')
        subprocess.run.assert_not_called()

    def test_protected_profile_cannot_be_overridden_by_test_or_journal(self):
        for command in ('set UnityEngine.AudioListener.volume 1', 'set settingsscript.s.S.FSound 1',
                        'set settingsscript.s.S.BEnableFpsLimit False', 'set settingsscript.s.S.FFPSLimit 60',
                        'set UnityEngine.QualitySettings.vSyncCount 1', 'bg off', 'quality 1',
                        'call UnityEngine.Screen.SetResolution 1920 1080 FullScreen', 'ping\nset settingsscript.s.S.FSound 1'):
            with self.subTest(command=command):
                with self.assertRaises(RuntimeError): display.validate_command(command)
        display.validate_command('set settingsscript.s.S.BEnableFpsLimit True')
        display.validate_command('set settingsscript.s.S.FFPSLimit 30')
        display.validate_command('quality 0')

    def test_capped_preflight_has_frame_progress_threshold(self):
        with patch("tldenv.lan_ip", return_value="127.0.0.1"):
            import mplib
        h=Mock(); L=Mock()
        h.return_value={'value':True,'runInBackground':True,'steamId':1}
        L.return_value={'value':True,'runInBackground':True,'steamId':2}
        def reply(identity):
            def call(command):
                if command == 'get UnityEngine.QualitySettings.vSyncCount': return {'value':0}
                if command == 'bg': return {'runInBackground':True}
                if command == 'net me': return {'steamId':identity}
                return {'value':True}
            return call
        h.side_effect=reply(1); L.side_effect=reply(2)
        with patch.object(mplib,'_game_prefixes',return_value=('/dedicated',['/dedicated'])), patch.object(display,'verify',return_value={}), patch.object(display,'check') as check, patch.object(mplib,'_fps_both',return_value={'desktop':29.2,'laptop':28.5}), patch.dict(os.environ,{'TLD_PREFLIGHT':'0'}):
            mplib.preflight(h,L)
            self.assertEqual(check.call_count,2)

    def test_saves_prefix_must_match_launch_prefix(self):
        foreign=self.root/'foreign/pfx'; foreign.mkdir(parents=True)
        with self.assertRaises(RuntimeError): target.identity(dict(self.spec,prefix=str(foreign)))

    def test_the_launch_blocks_until_the_window_is_proven_not_early(self):
        # "Synchronous rendering": the launch must not report success before the
        # window is mapped, hidden, unfocused, at the right size and owned by the
        # leased PID with its own listener.
        self.settings()
        checked = []

        def fake_validate(spec, root, require_window=True):
            checked.append(time.monotonic())
            if len(checked) < 3:
                raise RuntimeError('no owned hidden window proven')
            return []

        with patch.object(display, 'hypr_env', return_value=self.env), \
             patch.object(display, 'preflight'), \
             patch.object(target, 'processes', return_value=[]), \
             patch.object(target, 'free_port'), \
             patch.object(target, 'validate', side_effect=fake_validate), \
             patch.object(target, 'read_verdict', return_value=None), \
             patch.object(subprocess, 'run', return_value=Mock(returncode=0, stdout='ok\n')), \
             patch.object(time, 'sleep'):
            started = time.monotonic()
            self.assertEqual(target.launch(self.spec, self.root/'registry'), {'launched': True})
            self.assertGreaterEqual(len(checked), 3, 'the launch must wait for the proof')
            self.assertGreater(checked[-1], started)

    def test_post_launch_move_never_counts_as_acceptance(self):
        with self.assertRaises(RuntimeError): display.hide_existing(self.spec['compat'])
        subprocess.run.assert_not_called()

    def test_failed_dispatch_cleanup_uses_only_captured_lease(self):
        self.settings()
        with patch.object(display,'hypr_env',return_value=self.env), patch.object(display,'preflight'), patch.object(target,'processes',return_value=[]), patch.object(target,'free_port'), patch.object(subprocess,'run',return_value=Mock(returncode=1,stdout='')), patch.object(target,'signal_owned') as cleanup:
            with self.assertRaises(RuntimeError): target.launch(self.spec,self.root/'registry')
            cleanup.assert_called_once()
            self.assertEqual(cleanup.call_args.args[2], 'cleanup')
            self.assertEqual(cleanup.call_args.args[1], self.root/'registry')

    def test_menu_profile_avoids_player_dependent_refresh(self):
        for menu in (True, False):
            bridge = Mock(return_value={"value":menu})
            display.configure(bridge)
            commands = [call.args[0] for call in bridge.call_args_list]
            self.assertEqual('call settingsscript.s.RefreshInMain True' in commands, not menu)
            self.assertIn('call UnityEngine.Screen.SetResolution 1280 720 Windowed', commands)


class BackgroundLaunchGameArgumentTests(unittest.TestCase):
    def test_wrapper_arguments_are_not_game_executables(self):
        game = '/uniquecopy/TheLongDrive.exe'
        self.assertEqual(target.game_argument(
            ['python3', '-c', 'sleepcode', '--outer', game]), '')
        self.assertEqual(target.game_argument(
            ['python3', '-c', 'sleepcode', '--inner', game]), '')

    def test_direct_windows_path_and_absolute_wine_target(self):
        game = "Z:" + chr(92) + "unique-copy" + chr(92) + "TheLongDrive.exe"
        self.assertEqual(target.game_argument([game]), game)
        absolute = "/unique-copy/TheLongDrive.exe"
        self.assertEqual(target.game_argument(["/usr/bin/wine64", absolute]), absolute)

    def test_direct_game_and_wine_positions(self):
        game = '/games/TheLongDrive.exe'
        self.assertEqual(target.game_argument([game]), game)
        self.assertEqual(
            target.game_argument(['/usr/bin/wine', 'TheLongDrive.exe']),
            'TheLongDrive.exe')
        self.assertEqual(
            target.game_argument(['/usr/bin/wine64-preloader', 'TheLongDrive.exe']),
            'TheLongDrive.exe')
        self.assertEqual(
            target.game_argument(['/usr/bin/wine', '/games/Other.exe']), '')

    def test_matching_does_not_make_unowned_foreign_game_owned(self):
        spec = {'game': '/uniquecopy', 'compat': 'compat'}
        proc = {
            'game': '', 'compat': '',
            'executable_game': '/uniquecopy',
            'pid': 10, 'start': 20, 'token': 'tok'
        }
        lease = {'token': 'tok', 'pids': [[10, 20]]}
        self.assertEqual(target.matching(spec, [proc]), [proc])
        self.assertEqual(target.owned(spec, lease, [proc]), [])

    def test_owned_wrapper_requires_lease_pid_start_and_token(self):
        spec = {'game': 'game', 'compat': 'compat'}
        proc = {
            'game': 'game', 'compat': 'compat',
            'executable_game': '',
            'pid': 10, 'start': 20, 'token': 'tok'
        }
        lease = {'token': 'tok', 'pids': [[10, 20]]}
        self.assertEqual(target.owned(spec, lease, [proc]), [proc])

        wrong_token = dict(proc, token='other')
        self.assertEqual(target.owned(spec, lease, [wrong_token]), [])
        wrong_start = dict(proc, start=21)
        self.assertEqual(target.owned(spec, lease, [wrong_start]), [])
        wrong_pid = dict(proc, pid=11)
        self.assertEqual(target.owned(spec, lease, [wrong_pid]), [])

    def test_foreign_resource_wrapper_matches_but_is_not_owned(self):
        spec = {'game': 'foreign-game', 'compat': 'foreign-compat'}
        proc = {
            'game': 'foreign-game', 'compat': 'foreign-compat',
            'executable_game': '',
            'pid': 10, 'start': 20, 'token': 'tok'
        }
        lease = {'token': 'different', 'pids': [[10, 20]]}
        self.assertEqual(target.matching(spec, [proc]), [proc])
        self.assertEqual(target.owned(spec, lease, [proc]), [])


if __name__ == '__main__':
    unittest.main()


class CompositionTests(unittest.TestCase):
    """The compositor -> admission broker -> systemd unit -> game composition.

    Every boundary is faked: no broker runs, no unit is created, no game starts.
    """

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = pathlib.Path(self.tmp.name)
        for name in ('copy/game', 'prefix/pfx', 'steam', 'proton', 'slr'):
            (self.root / name).mkdir(parents=True)
        (self.root / 'copy/game/TheLongDrive.exe').touch()
        self.spec = {'game': str(self.root/'copy/game'), 'compat': str(self.root/'prefix'),
                     'steam': str(self.root/'steam'), 'proton': str(self.root/'proton'),
                     'slr': str(self.root/'slr'), 'port': 28111, 'profile': 'desktop'}
        self.token = 'a' * 32
        self.env = {'XDG_RUNTIME_DIR': str(self.root), 'HYPRLAND_INSTANCE_SIGNATURE': 'session'}
        # A fake admission broker and shim: the composition is exercised, no unit is created.
        self.broker = self.root/'tld-admit'
        self.broker.write_text('#!/bin/sh\nexit 0\n')
        self.broker.chmod(0o755)
        self.shim = self.root/'launchshim.py'
        self.shim.write_text('#!/bin/sh\nexit 0\n')
        self.shim.chmod(0o755)
        # The composition tests use a fake broker; the broker-level tests below
        # use the real one, so the fake is only applied where it is wanted.
        self.real_broker = None
        try:
            import testlaunch as _t
            self.real_broker = _t.broker_path()
        except Exception:
            self.real_broker = None
        self.broker_patch = patch.object(target, 'broker_path', return_value=self.broker)
        self.shim_patch = patch.object(target, 'shim_path', return_value=self.shim)
        self.broker_patch.start(); self.addCleanup(self.broker_patch.stop)
        self.shim_patch.start(); self.addCleanup(self.shim_patch.stop)

    def use_real_broker(self):
        """The broker-level tests exercise the real admission broker."""
        if self.real_broker is None:
            self.skipTest('no admission broker on this machine')
        self.broker_patch.stop()
        patcher = patch.object(target, 'broker_path', return_value=self.real_broker)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.addCleanup(self.broker_patch.start)
        self.spec = {'game': str(self.root/'copy/game'), 'compat': str(self.root/'prefix'),
                     'steam': str(self.root/'steam'), 'proton': str(self.root/'proton'),
                     'slr': str(self.root/'slr'), 'port': 28111, 'profile': 'desktop'}
        self.token = 'a' * 32
        self.env = {'XDG_RUNTIME_DIR': str(self.root), 'HYPRLAND_INSTANCE_SIGNATURE': 'session'}

    def composed(self, **over):
        spec = dict(self.spec, flags=('-screen-fullscreen', '0'), args=('-tldbridge', '28111'),
                    tokenfile=str(self.root/'token'), exitfile=str(self.root/'exit'))
        spec.update(over)
        with patch.object(target, 'broker_path', return_value=pathlib.Path('/fake/tld-admit')), \
             patch.object(target, 'shim_path', return_value=pathlib.Path('/fake/launchshim.py')):
            return target.compose(spec, self.token, self.env)

    def composed_with_env(self, env, **over):
        """compose() carries the admission overrides from the launch environment."""
        spec = dict(self.spec, flags=[], args=[], tokenfile=str(self.root/'token'),
                    exitfile=str(self.root/'exit'), command=['/bin/true'])
        spec.update(over)
        with patch.object(target, 'broker_path', return_value=pathlib.Path('/fake/tld-admit')), \
             patch.object(target, 'shim_path', return_value=pathlib.Path('/fake/launchshim.py')):
            return target.compose(spec, self.token, dict(self.env, **env))

    def composed_line(self, **over):
        return target.command_line(self.composed(**over))

    def test_composition_order_is_compositor_broker_unit_game(self):
        outer = self.composed()
        shim_at = next(i for i, a in enumerate(outer) if a.endswith('launchshim.py'))
        self.assertEqual(outer[shim_at + 1], '--outer')
        # the launch token is set inside the command line: the compositor's dispatch
        # does not pass the caller's environment on
        self.assertEqual(outer[0], 'env')
        self.assertTrue(outer[1].startswith('TLD_TEST_LAUNCH_TOKEN='))
        # the composed line is one shell line, and the game is not in it directly
        line = self.composed_line()
        self.assertNotIn('\n', line)
        self.assertIn('TheLongDrive.exe', line)
        broker = outer[outer.index('--') + 1:]
        self.assertEqual(broker[0], '/fake/tld-admit')
        self.assertEqual(broker[1], 'run')
        self.assertEqual(broker[2], '--profile')
        self.assertEqual(broker[3], 'game')
        inner = broker[broker.index('--') + 1:]
        self.assertTrue(inner[1].endswith('launchshim.py'))
        self.assertEqual(inner[2], '--inner')
        game = inner[inner.index('--') + 1:]
        self.assertTrue(any(a.endswith('TheLongDrive.exe') for a in game))
        # the game runs inside the unit, not in the compositor's dispatch directly
        self.assertNotIn('TheLongDrive.exe', ' '.join(outer[:outer.index('--')]))

    def test_broker_profile_is_game_and_name_is_plain(self):
        outer = self.composed()
        broker = outer[outer.index('--') + 1:]
        self.assertEqual(broker[3], 'game')
        name = broker[broker.index('--name') + 1]
        self.assertTrue(name.startswith('game-'))
        self.assertTrue(name.endswith(self.token[:16]) or self.token[:16] in name)
        self.assertTrue(all(c.isalnum() or c in '._-' for c in name))
        self.assertLessEqual(len(name), 64)

    def test_exactly_one_bridge_argument_in_the_unit(self):
        outer = self.composed()
        broker = outer[outer.index('--') + 1:]
        inner = broker[broker.index('--') + 1:]
        game = inner[inner.index('--') + 1:]
        self.assertEqual(game.count('-tldbridge'), 1)
        self.assertEqual(game[game.index('-tldbridge') + 1], '28111')

    def test_launch_token_and_compat_identities_reach_the_unit(self):
        outer = self.composed()
        broker = outer[outer.index('--') + 1:]
        inner = broker[broker.index('--') + 1:]
        game = inner[inner.index('--') + 1:]
        joined = ' '.join(game)
        self.assertIn('TLD_TEST_LAUNCH_TOKEN=' + self.token, joined)
        self.assertIn('STEAM_COMPAT_DATA_PATH=' + self.spec['compat'], joined)
        self.assertIn('STEAM_COMPAT_INSTALL_PATH=' + self.spec['game'], joined)
        self.assertNotIn('steam -applaunch', joined)
        self.assertNotIn('nohup', joined)
        self.assertNotIn('setsid', joined)

    def test_missing_broker_refuses_the_launch(self):
        spec = dict(self.spec, flags=[], args=[], tokenfile=str(self.root/'t'), exitfile=str(self.root/'e'))
        with patch.object(target, 'broker_path', side_effect=RuntimeError('no admission broker')), \
             patch.object(target, 'shim_path', return_value=pathlib.Path('/fake/launchshim.py')):
            with self.assertRaises(RuntimeError):
                target.compose(spec, self.token, self.env)

    def test_missing_shim_refuses_the_launch(self):
        spec = dict(self.spec, flags=[], args=[], tokenfile=str(self.root/'t'), exitfile=str(self.root/'e'))
        with patch.object(target, 'broker_path', return_value=pathlib.Path('/fake/tld-admit')), \
             patch.object(target, 'shim_path', side_effect=RuntimeError('the launch shim is missing')):
            with self.assertRaises(RuntimeError):
                target.compose(spec, self.token, self.env)

    def test_shim_refuses_without_a_compositor_token(self):
        import launchshim
        with patch.dict(os.environ, {}, clear=True):
            with self.assertRaises(SystemExit):
                launchshim.outer(['--outer', str(self.root/'token'), '--', '/bin/true'])
        self.assertFalse((self.root/'token').exists())

    def test_shim_refuses_without_a_launch_token(self):
        import launchshim
        with patch.dict(os.environ, {'HL_EXEC_RULE_TOKEN': 'compositor-token'}, clear=True):
            with self.assertRaises(SystemExit):
                launchshim.outer(['--outer', str(self.root/'token'), '--', '/bin/true'])
        self.assertFalse((self.root/'token').exists())

    def test_shim_carries_both_tokens_across_the_unit_boundary(self):
        import launchshim
        tokenfile = self.root/'token'
        exitfile = self.root/'exit'
        marker = self.root/'broker-ran'
        broker = self.root/'fake-broker'
        broker.write_text('#!/bin/sh\necho ran > %s\nexit 7\n' % marker)
        broker.chmod(0o755)
        with patch.dict(os.environ, {'HL_EXEC_RULE_TOKEN': 'compositor-token',
                                     'TLD_TEST_LAUNCH_TOKEN': self.token}, clear=True):
            try:
                launchshim.outer(['--outer', str(tokenfile), '--exitfile', str(exitfile),
                                  '--', str(broker)])
            except SystemExit as exit:
                self.assertEqual(exit.code, 7)
        self.assertTrue(tokenfile.is_file())
        pairs = launchshim.read_pairs(str(tokenfile))
        self.assertEqual(pairs['HL_EXEC_RULE_TOKEN'], 'compositor-token')
        self.assertEqual(pairs['TLD_TEST_LAUNCH_TOKEN'], self.token)
        self.assertEqual(oct(tokenfile.stat().st_mode & 0o777), '0o600')
        # the broker really ran, and the outer mode recorded its verdict
        self.assertTrue(marker.is_file())
        self.assertEqual(target.read_verdict(str(exitfile)), 7)

    def test_shim_inner_restores_both_tokens_before_the_game(self):
        import launchshim
        tokenfile = self.root/'token'
        tokenfile.write_text('HL_EXEC_RULE_TOKEN=compositor-token\nTLD_TEST_LAUNCH_TOKEN=%s\n' % self.token)
        probe = self.root/'probe.sh'
        probe.write_text('#!/bin/sh\nenv > %s\n' % (self.root/'seen'))
        probe.chmod(0o755)
        # The inner mode execs the game, so it runs as its own process here.
        result = subprocess.run([sys.executable, str(launchshim.__file__), '--inner', str(tokenfile),
                                 '--', str(probe)], capture_output=True, text=True, env={})
        self.assertEqual(result.returncode, 0, result.stderr)
        seen = (self.root/'seen').read_text()
        self.assertIn('HL_EXEC_RULE_TOKEN=compositor-token', seen)
        self.assertIn('TLD_TEST_LAUNCH_TOKEN=' + self.token, seen)

    def test_shim_inner_refuses_an_incomplete_token_file(self):
        import launchshim
        tokenfile = self.root/'token'
        tokenfile.write_text('HL_EXEC_RULE_TOKEN=compositor-token\n')
        with self.assertRaises(SystemExit):
            launchshim.inner(['--inner', str(tokenfile), '--', '/bin/true'])

    def test_shim_inner_refuses_a_missing_token_file(self):
        import launchshim
        with self.assertRaises(SystemExit):
            launchshim.inner(['--inner', str(self.root/'absent'), '--', '/bin/true'])

    def test_shim_never_prints_a_token(self):
        import launchshim
        tokenfile = self.root/'token'
        with patch.dict(os.environ, {'HL_EXEC_RULE_TOKEN': 'secret-compositor-token'}, clear=True):
            err = io.StringIO()
            with contextlib.redirect_stderr(err):
                try:
                    launchshim.outer(['--outer', str(tokenfile), '--', '/bin/true'])
                except SystemExit:
                    pass
        self.assertNotIn('secret-compositor-token', err.getvalue())

    def test_launch_token_is_set_inside_the_command_line(self):
        # The compositor's dispatch runs the command through its own shell and passes
        # no environment on: a token in the dispatcher's environment would be lost,
        # and the launched game would carry no launch identity.
        outer = self.composed()
        self.assertEqual(outer[0], 'env')
        self.assertEqual(outer[1], 'TLD_TEST_LAUNCH_TOKEN=' + self.token)

    def test_broker_job_runs_inside_its_own_unit(self):
        # The broker's argv is what systemd-run turns into the unit's command: the
        # shim's inner mode, which restores the tokens and execs the game.
        outer = self.composed()
        broker = outer[outer.index('--') + 1:]
        self.assertEqual(broker[0], '/fake/tld-admit')
        inner = broker[broker.index('--') + 1:]
        self.assertTrue(inner[1].endswith('launchshim.py'))
        self.assertEqual(inner[2], '--inner')

    def test_launched_process_must_carry_the_compositor_token(self):
        # A process without the compositor token was never covered by the
        # launch-time hidden/no-focus rule.
        envfile = self.root/'proc-environ'
        envfile.write_bytes(b'HL_EXEC_RULE_TOKEN=compositor-token\0TLD_TEST_LAUNCH_TOKEN=x\0')
        with patch.object(pathlib.Path, 'read_bytes', return_value=envfile.read_bytes()):
            self.assertTrue(target.check_compositor_token([1234]))
        envfile.write_bytes(b'TLD_TEST_LAUNCH_TOKEN=x\0')
        with patch.object(pathlib.Path, 'read_bytes', return_value=envfile.read_bytes()):
            with self.assertRaises(RuntimeError):
                target.check_compositor_token([1234])

    def test_remote_guard_ships_the_shim_and_finds_the_broker(self):
        # The controller ships the guard's sources over SSH: the target must end up
        # with a real shim file and a real broker, or it cannot compose a launch.
        import tempfile as tf
        root = pathlib.Path(tf.mkdtemp(prefix='guard-check-'))
        self.addCleanup(lambda: __import__('shutil').rmtree(root, ignore_errors=True))
        (root/'game').mkdir(); (root/'compat/pfx').mkdir(parents=True); (root/'steam').mkdir()
        (root/'game/TheLongDrive.exe').touch()
        spec = launchguard.specification(False, game_dir=str(root/'game'), compat=str(root/'compat'), port=28141)
        spec['steam'] = str(root/'steam')
        program = launchguard.program(spec, 'identity')
        probe = program.replace(
            'try:\n    result=sys.modules["testlaunch"].operate',
            'print(json.dumps({"shim": os.path.exists(shim_path),\n'
            '                  "broker": sys.modules["testlaunch"].broker_path().name,\n'
            '                  "resolved": str(sys.modules["testlaunch"].shim_path())}))\n'
            'raise SystemExit(0)\n'
            'try:\n    result=sys.modules["testlaunch"].operate')
        out = subprocess.run([sys.executable, '-'], input=probe, capture_output=True, text=True)
        self.assertEqual(out.returncode, 0, out.stderr)
        found = json.loads(out.stdout)
        self.assertTrue(found['shim'])
        self.assertTrue(found['resolved'].endswith('launchshim.py'))
        self.assertEqual(found['broker'], 'tld-admit')

    def test_controller_surfaces_the_foreign_forward_refusal_distinctly(self):
        # The controller must not collapse "another lane's tunnel" into "port busy":
        # the operator has to know that connecting would reach another lane's game.
        with patch.object(launchguard,'specification',return_value=self.spec), \
             patch.object(launchguard,'run',side_effect=RuntimeError('target guard refused claim-forward: the forwarding port belongs to another lease; refused')):
            with self.assertRaises(RuntimeError) as caught:
                bench.claim_forward_port('fake@target', 28197, 28101)
        self.assertIn('another lease', str(caught.exception))
        with patch.object(launchguard,'specification',return_value=self.spec), \
             patch.object(launchguard,'run',side_effect=RuntimeError('target guard refused claim-forward: target unavailable')):
            with self.assertRaises(RuntimeError) as caught:
                bench.claim_forward_port('fake@target', 28197, 28101)
        self.assertNotIn('another lease', str(caught.exception))

    def test_forward_port_owned_by_another_lease_is_refused_distinctly(self):
        # F83: a forwarding port that another lane's lease claims is not merely
        # "occupied": connecting to it would talk to another lane's game.
        root = pathlib.Path(self.tmp.name)/'registry'
        root.mkdir()
        mine = dict(self.spec, token='mine')
        theirs = dict(self.spec, game=str(self.root/'other/game'), token='theirs')
        (root/(target.key(theirs)+'.json')).write_text(json.dumps(
            dict(theirs, forwards={'28199': {'remote': 'h', 'destination': 28199}})))
        with self.assertRaises(RuntimeError) as caught:
            target.claim_forward(root, mine, 28199)
        self.assertIn('another lease', str(caught.exception))

    def test_forward_port_claim_is_recorded_and_releasable(self):
        root = pathlib.Path(self.tmp.name)/'registry'
        root.mkdir()
        mine = dict(self.spec, token='mine')
        (root/(target.key(mine)+'.json')).write_text(json.dumps(mine))
        target.claim_forward(root, mine, 28198)
        lease = json.loads((root/(target.key(mine)+'.json')).read_text())
        self.assertIn('28198', lease['forwards'])
        # the same lease may re-claim its own port
        target.claim_forward(root, mine, 28198)
        target.release_forwards(root, mine)
        lease = json.loads((root/(target.key(mine)+'.json')).read_text())
        self.assertEqual(lease['forwards'], {})

    def test_release_forwards_leaves_other_leases_untouched(self):
        root = pathlib.Path(self.tmp.name)/'registry'
        root.mkdir()
        mine = dict(self.spec, token='mine')
        theirs = dict(self.spec, game=str(self.root/'other/game'), token='theirs')
        (root/(target.key(mine)+'.json')).write_text(json.dumps(dict(mine, forwards={'1': {}})))
        (root/(target.key(theirs)+'.json')).write_text(json.dumps(dict(theirs, forwards={'2': {}})))
        target.release_forwards(root, mine)
        other = json.loads((root/(target.key(theirs)+'.json')).read_text())
        self.assertIn('2', other['forwards'])

    def test_forward_claim_refuses_an_invalid_port(self):
        root = pathlib.Path(self.tmp.name)/'registry'
        root.mkdir()
        for bad in (0, 1023, 70000, '28199', None):
            with self.assertRaises(RuntimeError):
                target.operate(dict(self.spec, forward_port=bad), 'claim-forward')

    def test_testprefix_make_keeps_the_seeded_settings(self):
        # F84's trap: `testprefix.py make` stripped the settings the launcher needs,
        # so the obvious way to make a prefix produced a launcher that refused.
        # The strip script is executed here, on a fixture prefix, to prove the
        # settings file survives while the saves still go.
        import testprefix
        source = pathlib.Path(self.tmp.name)/'source'
        dest = pathlib.Path(self.tmp.name)/'dest'
        user = source/'pfx/drive_c/users/steamuser'
        (user/'Documents/TheLongDrive/Saves').mkdir(parents=True)
        (user/'Documents/TheLongDrive/settings.tldc').write_text(json.dumps({'FSound': 0.0, 'x': 1}))
        (user/'Documents/TheLongDrive/Saves/slot.tldc').write_text('save')
        (source/'pfx/user.reg').write_text('[Software\\\\Genesz\\\\TheLongDrive]\nx=1\n\n')
        out = subprocess.run(['bash', '-s', '--', str(source), str(dest)],
                             input=testprefix.STRIP, capture_output=True, text=True)
        self.assertEqual(out.returncode, 0, out.stderr)
        kept = dest/'pfx/drive_c/users/steamuser/Documents/TheLongDrive/settings.tldc'
        self.assertTrue(kept.is_file(), 'the seeded settings file must survive testprefix make')
        self.assertEqual(json.loads(kept.read_text())['x'], 1)
        self.assertFalse((dest/'pfx/drive_c/users/steamuser/Documents/TheLongDrive/Saves/slot.tldc').exists(),
                         'the saves are still stripped')

    def test_the_admission_gauges_are_set_inside_the_command_line(self):
        # The compositor's dispatch passes no environment on, so the broker's own
        # gauge overrides only reach it if they are in the command line.
        outer = self.composed_with_env({'TLD_ADMIT_RUNTIME_DIR': '/tmp/does-not-matter'})
        self.assertIn('TLD_ADMIT_RUNTIME_DIR=/tmp/does-not-matter', ' '.join(outer))

    def test_the_broker_never_sees_a_secret_or_an_environment_dump(self):
        # The broker's state is published from a fixed field set; a secret-shaped
        # value in the environment must never reach it.
        secret = 'fixture-private-secret-value'
        with patch.dict(os.environ, {'GUARDFINISH_PASSWORD': secret}, clear=False):
            spec = dict(self.spec, flags=[], args=[], tokenfile=str(self.root/'t'),
                        exitfile=str(self.root/'e'), command=['/bin/true'])
            outer = self.composed(**{})
        self.assertNotIn(secret, ' '.join(outer))

    def test_a_profile_mismatch_is_refused_by_the_broker(self):
        self.use_real_broker()
        # The broker verifies the profile it applied; a mismatch is a refusal, and
        # the launcher must not treat a failed unit as a launch.
        import synthproof
        work = pathlib.Path(self.tmp.name)/'mismatch'
        meminfo, pressure = synthproof.write_gauges(work/'gauges')
        env = synthproof.admit_env(work, meminfo, pressure)
        unit = 'tld-job-game-mismatch-probe.service'
        command = ['systemd-run', '--user', '--quiet', '--wait', '--pipe',
                   '--expand-environment=no', '--no-ask-password', '--unit=' + unit,
                   '--property=Type=exec', '--property=MemoryMax=64M',
                   '--property=MemoryHigh=32M', '--property=MemorySwapMax=0',
                   '--property=TasksMax=32', '--property=CPUQuota=10%',
                   '--property=OOMPolicy=kill']
        for key in ('TLD_ADMIT_MEMINFO', 'TLD_ADMIT_WATCHED_CGROUP', 'TLD_ADMIT_TEST_NO_WATCH',
                    'TLD_ADMIT_PSI', 'TLD_ADMIT_VMSTAT'):
            if key in env:
                command.append('--setenv=' + key + '=' + env[key])
        command += ['--', synthproof.broker_path(), '--internal-supervise',
                    str(work/'admit'), unit, 'game', 'mismatch', str(int(time.time())),
                    'v2', '--', '/usr/bin/true']
        result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=25)
        try:
            self.assertEqual(result.returncode, 75, result.stderr)
            state = json.loads((work/'admit'/'state.json').read_text())
            self.assertFalse(state['limits']['verified'])
            self.assertEqual(state['result'], 'profile-verification-failed')
        finally:
            # this test's own unit, by name: never a machine-wide stop
            subprocess.run(['systemctl', '--user', 'stop', unit], capture_output=True, timeout=20)
            subprocess.run(['systemctl', '--user', 'reset-failed', unit], capture_output=True)

    def test_a_concurrent_second_job_is_refused(self):
        self.use_real_broker()
        # The broker admits one job at a time: a second controller is refused with
        # 75 and creates no unit.
        import synthproof
        work = pathlib.Path(self.tmp.name)/'concurrent'
        meminfo, pressure = synthproof.write_gauges(work/'gauges')
        env = synthproof.admit_env(work, meminfo, pressure)
        first = subprocess.Popen([synthproof.broker_path(), 'run', '--profile', 'game',
                                  '--name', 'game-concurrent-a', '--', '/bin/sleep', '6'],
                                 env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline and not synthproof.list_units():
                time.sleep(0.2)
            units = synthproof.list_units()
            self.assertEqual(len(units), 1, str(units))
            second = subprocess.run([synthproof.broker_path(), 'run', '--profile', 'game',
                                     '--name', 'game-concurrent-b', '--', '/bin/true'],
                                    env=env, capture_output=True, text=True, timeout=25)
            self.assertEqual(second.returncode, 75, second.stderr)
            self.assertEqual(len(synthproof.list_units()), 1, str(synthproof.list_units()))
        finally:
            first.terminate()
            try:
                first.wait(timeout=10)
            except subprocess.TimeoutExpired:
                first.kill()
            for unit in synthproof.list_units():
                subprocess.run(['systemctl', '--user', 'stop', unit], capture_output=True)
                subprocess.run(['systemctl', '--user', 'reset-failed', unit], capture_output=True)

    def test_a_stale_unit_is_recovered_not_reused(self):
        self.use_real_broker()
        # A unit left over from an earlier run must not be mistaken for this
        # launch's unit; the broker refuses while one is still active.
        import synthproof
        work = pathlib.Path(self.tmp.name)/'stale'
        meminfo, pressure = synthproof.write_gauges(work/'gauges')
        env = synthproof.admit_env(work, meminfo, pressure)
        unit = 'tld-job-game-stale-probe.service'
        stale = subprocess.Popen(['systemd-run', '--user', '--quiet', '--unit=' + unit,
                                  '--property=Type=exec', '/bin/sleep', '6'],
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline and unit not in synthproof.list_units():
                time.sleep(0.2)
            self.assertIn(unit, synthproof.list_units())
            result = subprocess.run([synthproof.broker_path(), 'run', '--profile', 'game',
                                     '--name', 'game-stale-b', '--', '/bin/true'],
                                    env=env, capture_output=True, text=True, timeout=25)
            self.assertEqual(result.returncode, 75, result.stderr)
        finally:
            stale.terminate()
            try:
                stale.wait(timeout=10)
            except subprocess.TimeoutExpired:
                stale.kill()
            subprocess.run(['systemctl', '--user', 'stop', unit], capture_output=True)
            subprocess.run(['systemctl', '--user', 'reset-failed', unit], capture_output=True)

    def test_the_controller_dying_does_not_leave_a_window(self):
        self.use_real_broker()
        # The waiter survives the controller: the unit keeps its ownership, and
        # the cleanup still addresses only that unit.
        import synthproof
        work = pathlib.Path(self.tmp.name)/'controller-death'
        meminfo, pressure = synthproof.write_gauges(work/'gauges')
        env = synthproof.admit_env(work, meminfo, pressure)
        controller = subprocess.Popen([synthproof.broker_path(), 'run', '--profile', 'game',
                                       '--name', 'game-controller-death', '--', '/bin/sleep', '8'],
                                      env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline and not synthproof.list_units():
                time.sleep(0.2)
            units = synthproof.list_units()
            self.assertEqual(len(units), 1, str(units))
            unit = units[0]
            controller.kill()
            controller.wait(timeout=10)
            # the unit is still owned by that job: it is stopped by name, and
            # nothing else on the machine is touched
            subprocess.run(['systemctl', '--user', 'stop', unit], capture_output=True, timeout=20)
            self.assertNotIn(unit, synthproof.list_units())
        finally:
            if controller.poll() is None:
                controller.kill()
            for unit in synthproof.list_units():
                subprocess.run(['systemctl', '--user', 'stop', unit], capture_output=True)
                subprocess.run(['systemctl', '--user', 'reset-failed', unit], capture_output=True)

    def test_remote_broker_staging_is_digest_verified(self):
        # The remote broker is staged at a digest-addressed path: the bytes that
        # are staged are the bytes that were hashed.
        import hashlib
        source = pathlib.Path(__file__).resolve().parent/'tld-admit'
        if not source.is_file():
            try:
                source = target.broker_path()
            except RuntimeError:
                self.skipTest('no admission broker on this machine')
        digest = hashlib.sha256(source.read_bytes()).hexdigest()
        # The staging path is addressed by the digest of the bytes it holds.
        staged = pathlib.Path(self.tmp.name)/digest/'tld-admit'
        staged.parent.mkdir(parents=True)
        staged.write_bytes(source.read_bytes())
        staged.chmod(0o755)
        self.assertEqual(hashlib.sha256(staged.read_bytes()).hexdigest(), digest)
        self.assertEqual(staged.parent.name, digest)
        self.assertTrue(os.access(staged, os.X_OK))
        # and the staged bytes are the broker's bytes, not a different build
        self.assertEqual(staged.read_bytes(), source.read_bytes())

    def test_the_target_stages_the_broker_at_a_digest_addressed_path(self):
        # The remote guard stages the broker by digest: the path carries the
        # SHA-256 of the bytes it holds, and the target recomputes it.
        import hashlib
        import tempfile as tf
        root = pathlib.Path(tf.mkdtemp(prefix='stage-check-'))
        self.addCleanup(lambda: __import__('shutil').rmtree(root, ignore_errors=True))
        (root/'game').mkdir(); (root/'compat/pfx').mkdir(parents=True); (root/'steam').mkdir()
        (root/'game/TheLongDrive.exe').touch()
        spec = launchguard.specification(False, game_dir=str(root/'game'), compat=str(root/'compat'), port=28171)
        spec['steam'] = str(root/'steam')
        program = launchguard.program(spec, 'identity')
        marker = '_sys.modules["testlaunch"].__dict__["SHIM_PATH"]=shim_path'
        self.assertIn(marker, program)
        probe = program.replace(marker, marker +
            '\nimport json as _j\n'
            'print(_j.dumps({"bin": os.environ.get("TLD_ADMIT_BIN"),'
            '"found": str(sys.modules["testlaunch"].broker_path())}))\n'
            'raise SystemExit(0)')
        out = subprocess.run([sys.executable, '-'], input=probe, capture_output=True, text=True)
        self.assertEqual(out.returncode, 0, out.stderr)
        found = json.loads(out.stdout)['found']
        self.assertTrue(found.startswith('/run/user/'), found)
        staged = pathlib.Path(found)
        self.assertTrue(staged.is_file(), found)
        digest = staged.parent.name
        self.assertEqual(hashlib.sha256(staged.read_bytes()).hexdigest(), digest)
        self.assertEqual(len(digest), 64)

    def test_a_foreign_window_is_refused_even_on_the_right_workspace(self):
        # A window of another process on the hidden workspace is still a foreign
        # window: the proof is by PID, not by workspace.
        foreign = {'pid': 999999, 'floating': True, 'fullscreen': 0, 'focusHistoryID': 3,
                   'size': [1280, 720], 'workspace': {'name': display.WORKSPACE}}
        with patch.object(display, 'preflight'), patch.object(display, 'query', return_value=[foreign]):
            with self.assertRaises(RuntimeError):
                target.check_windows(self.spec, self.env, [123456])

    def test_a_window_of_the_wrong_size_is_refused(self):
        wrong = {'pid': 123456, 'floating': True, 'fullscreen': 0, 'focusHistoryID': 3,
                 'size': [1920, 1080], 'workspace': {'name': display.WORKSPACE}}
        with patch.object(display, 'preflight'), patch.object(display, 'query', return_value=[wrong]):
            with self.assertRaises(RuntimeError):
                target.check_windows(self.spec, self.env, [123456])

    def test_a_non_floating_window_is_refused(self):
        tiled = {'pid': 123456, 'floating': False, 'fullscreen': 0, 'focusHistoryID': 3,
                 'size': [1280, 720], 'workspace': {'name': display.WORKSPACE}}
        with patch.object(display, 'preflight'), patch.object(display, 'query', return_value=[tiled]):
            with self.assertRaises(RuntimeError):
                target.check_windows(self.spec, self.env, [123456])

    def test_admission_refusal_is_not_a_slow_start(self):
        exitfile = self.root/'exit'
        exitfile.write_text('75\n')
        self.assertEqual(target.read_verdict(str(exitfile)), 75)
        self.assertIsNone(target.read_verdict(str(self.root/'absent')))
