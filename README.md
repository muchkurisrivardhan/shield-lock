# ShieldLock

Animated wallpaper reveals around the Windows sign-in, plus a S.H.I.E.L.D.-style intruder alert.

- **Every unlock** plays a 10-second reveal animation of your current wallpaper, drawn over the
  desktop (click-through, never steals focus), ending in a dissolve to the real thing.
- **Set a new wallpaper** and it is detected instantly: the new animation is previewed, rendered to an
  MP4 in [`renders/`](renders/) and pushed to this repo automatically.
- **Wrong PIN** while the PC was locked? A full-screen red *UNAUTHORIZED ACCESS* alert plays the
  moment you unlock. (Windows does not let apps draw on the lock screen itself.)

The effect is chosen from a fingerprint of the picture, so each wallpaper always gets the same one:
**mosaic** (tiles fly in), **scan beam**, **pixelate**, **strips** or **iris**. The minimalist
"Wall. Paper." wallpaper gets a hand-made animation: bricks are laid one by one, a sheet of paper
flutters down and the caption types itself.

## Build

No SDK needed. Run `build.cmd`: it uses the C# compiler that ships with Windows (.NET Framework 4.8)
and produces `ShieldLock.exe`. Rendering videos needs [ffmpeg](https://ffmpeg.org/) on `PATH`.

## Run

```
ShieldLock.exe                  tray app: watches sign-ins and wallpaper changes
ShieldLock.exe --test           plays the alert and the intro once, then exits
ShieldLock.exe --render         renders the current wallpaper's animation to renders/ and exits
```

Right-click the tray icon for *Test animation*, *Re-render wallpaper video*, *Open renders folder*.
Activity is logged to `shieldlock.log` next to the exe.

### Start at sign-in

The tray app only lives until the PC shuts down, so register it once:

```
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v ShieldLock /d "C:\path	o\ShieldLock.exe" /f
```

Remove it any time in Task Manager -> Startup apps. No admin needed.

## Files

- `Program.cs` – everything: sign-in watcher, wallpaper detection, the five effects, video rendering, git push
- `ShieldAlert.xaml` – the red intruder alert
- `build.cmd` – one-line build
