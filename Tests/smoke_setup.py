import os, subprocess, tempfile, time, urllib.request, urllib.parse, urllib.error, json
from pathlib import Path
root = Path(__file__).resolve().parent.parent
with tempfile.TemporaryDirectory(prefix='gloomhaven-http-') as temp:
    env = dict(os.environ, Data__DbPath=str(Path(temp)/'test.db'), ASPNETCORE_ENVIRONMENT='Production')
    log_path = Path(temp)/'startup.log'
    with log_path.open('w+') as log:
        proc = subprocess.Popen(['dotnet', str(root/'JankDiscordBot/bin/Debug/net8.0/JankDiscordBot.dll'), '--urls', 'http://127.0.0.1:15061'], cwd=root/'JankDiscordBot', env=env, stdout=log, stderr=log, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        try:
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            base = 'http://127.0.0.1:15061/Setup'
            for i in range(15):
                try:
                    page = opener.open(base, timeout=1).read().decode(); break
                except OSError:
                    if proc.poll() is not None:
                        log.flush(); log.seek(0)
                        raise Exception('App startup failed: ' + log.read())
                    time.sleep(.2)
            else:
                log.flush(); log.seek(0)
                raise Exception('App did not start: ' + log.read())
            def post(data):
                try:
                    response = opener.open(base+'?handler=Autosave', urllib.parse.urlencode(data).encode(), timeout=10)
                    return response.status, json.loads(response.read())
                except urllib.error.HTTPError as error:
                    return error.code, json.loads(error.read())
            assert 'Calendar event' in page and 'Announcements disabled' in page
            from html.parser import HTMLParser
            class Inputs(HTMLParser):
                def __init__(self): super().__init__(); self.inputs=[]
                def handle_starttag(self, tag, attrs):
                    if tag == 'input': self.inputs.append(dict(attrs))
            status, result = post(dict(scope='calendar', CalendarEnabled='true', EventTitle='Game night', EventLocation='', EventAddress='', EventDescription='', EventDuration='180', EventReminder='', EventIncludeNote='false', EventIncludeAssignments='false'))
            assert status == 200, (status, result)
            page = opener.open(base).read().decode()
            parser = Inputs(); parser.feed(page)
            fields = {i.get('name'): i for i in parser.inputs if i.get('type') != 'hidden'}
            assert 'checked' not in fields['EventIncludeNote'] and 'checked' not in fields['EventIncludeAssignments']
            assert fields['EventReminder'].get('value', '') == ''
            assert fields['EventDuration']['value'] == '180' and 'checked' in fields['CalendarEnabled']
            status, result = post(dict(scope='announcements', AnnounceChannelId='', AnnounceTime='08:15', AnnouncementMode='Weekday', AnnouncementWeekday='Monday'))
            assert status == 200 and 'disabled' in result['preview'], (status, result)
            status, result = post(dict(scope='announcements', AnnounceChannelId='123', AnnounceTime='08:15', AnnouncementMode='Weekday', AnnouncementWeekday='Monday'))
            assert status == 200 and result['preview'], (status, result)
            status, result = post(dict(scope='announcements', AnnounceChannelId='123', AnnounceTime='08:15', AnnouncementMode='Weekday', AnnouncementWeekday='Invalid'))
            assert status == 400, (status, result)
            for duration, reminder in [('nope','30'),('180','-1')]:
                status, result = post(dict(scope='calendar', EventTitle='Game night', EventDuration=duration, EventReminder=reminder))
                assert status == 400, (status, result)
            print('Passed Setup HTTP smoke checks: rendering, optional fields, unchecked options, persistence, preview, and invalid input rejection.')
        finally:
            if proc.poll() is None: proc.terminate()
            proc.wait(timeout=10)
