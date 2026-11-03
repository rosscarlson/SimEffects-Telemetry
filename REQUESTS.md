# Requests

What was asked, word for word, by date.

## 2026-11-02

# Project Brief: Gran Turismo Telemetry

## Locations
- Working directory: C:\Users\ross\OneDrive\Code\Windows\GranTurismoTelemetry
- Visual design guide: C:\Users\ross\OneDrive\Code\Windows\design.md (follow it for all UI)
- PCSX2: G:\Tools\PCSX2
- Game: G:\ROMs\Gran Turismo 4 Spec II v1.10 (patched GT4 Online Public Beta, NTSC-U, SCUS-97436)

Never modify the PCSX2 install or the ROM directly. Back up any PCSX2 config or patch file before changing it.

## Overview
Build a Windows app that adds real-time telemetry output, ultrawide/triple-screen rendering, and live FOV/seat adjustment to emulated Gran Turismo games.
- Phase 1 (now): Gran Turismo 4 Spec II on PCSX2.
- Later: Gran Turismo 5 and Gran Turismo 6 on RPCS3.
- Design the architecture and UI for multiple games from day one. It should also be able to absorb the Forza work Ross already did: telemetry for Forza Horizon 1–3 via the Pinyon Shift recomp, and Forza Motorsport 4 via a helper app for Azimuth.

## Architecture
- Game-agnostic core: telemetry model, UDP output, hotkeys, profiles, UI.
- Per-game adapters, each declaring which features it supports:
  - memory source (PCSX2 PINE, RPCS3 PINE, or a native process)
  - address/struct map
  - camera controls
  - display patches
- Suggested stack is C#/.NET (WPF), matching Ross's other Windows tools. Propose an alternative if there is a strong reason.

## Goal 1: Telemetry
- Read emulated memory through PCSX2's PINE IPC interface. Ross will enable it under Settings > Advanced; use the default slot and make it configurable.
- Poll at about 60 Hz and send UDP to a configurable IP and port.
- Output format: start with a format SimHub already understands natively (e.g. Forza "Data Out" Dash). Keep the format pluggable so a custom format can be added later.
- Fields, in priority order:
  1. RPM, max RPM, speed, gear, throttle, brake, clutch, steering
  2. Lap, lap times, race position, race state (in race, paused, menu)
  3. Velocity, acceleration/g-forces, orientation
  4. Per-wheel suspension travel, wheel slip/rotation, surface type/rumble (for haptics)
- Discovery process:
  - Ross has a save state mid-race and will drive on cue (accelerate, shift, brake, hit kerbs) while you scan memory.
  - Build small scanning/diff tooling as part of the project.
  - Prefer stable struct bases or pointers that survive save-state loads, track changes and car changes.
  - Spec II offsets differ from retail GT4 and other GT4 mods, so don't trust cheat codes made for other versions without verifying them.
  - Document every confirmed address and struct layout in the repo.

## Goal 2: Ultrawide and triples
- Targets: 32:9 at 5120x1440 (center monitor) and 64:9 at 10240x1440 (three 2560x1440 panels).
- Investigate existing community widescreen/FOV pnach patches for Spec II, PCSX2's own aspect-ratio and widescreen settings, and live memory writes through PINE.
- Fix aspect ratio and HUD/menu scaling rather than stretching the image.
- Triples will be one wide camera. Research whether any per-screen angled projection is feasible, but don't block on it.

## Goal 3: Live FOV and seating position
- Keyboard shortcuts that work while the game window has focus. They should adjust FOV and camera position (forward/back, up/down, left/right, possibly pitch) in real time by writing camera values through PINE.
- Show a small on-screen or overlay indicator of current values while adjusting.
- Save values to profiles per game, with per-car profiles if car ID is readable. Load them automatically.
- Base GT4 has no true cockpit view. Find out what camera views Spec II offers, and find the best view to anchor a cockpit-like seat position on.

## UI
- Main window with:
  - game/adapter selector
  - connection status
  - live telemetry readout (for verifying addresses)
  - UDP settings
  - display settings
  - hotkey bindings
  - profiles
- System tray mode with an option to run at startup.
- Show features a game's adapter doesn't support as unavailable rather than hiding them.

## Milestones
1. Connect to PCSX2 over PINE and read/display raw memory; build scanning tools. - work with Ross on this as the first step, the tool you'll code should use audio to tell Ross what he needs to do, if you need to "watch" while he drives. Let him know before you're ready so he can get everything in game for you. Once this step is complete we'll move on to the following, don't code this now it's only for planning:

2. Map and verify the priority 1–2 telemetry fields; get UDP output working; confirm SimHub receives it.
3. Map the camera values; implement live FOV/seat hotkeys and profiles.
4. Get 32:9 and 64:9 rendering working correctly.
5. Map the priority 3–4 telemetry fields (physics and per-wheel data).
6. Prepare the adapter layer for RPCS3 (GT5/GT6) and Forza sources.

If you have questions or need my attention speak to me - otherwise let me know when you have a first version of our memory sampling app.

## 2026-11-02

continue

go ahead and get in game/car now?

I had to stop my wheel isn't setup - I need to bind it

[image 1]My throttle and brake axis are backwards in game - what to change?

I closed it, fix it for me please and tell me when I can relaunch

brake/throttle work now - steering doesn't, can you check that? FFB seems to and the buttons work but the wheel axis seems to not

closed

Great - it's driving just fine with FFB, what are the next steps for you to watch memory? If you're ready for that speak to me and tell me what to step through

ready for gears - as for pedals they are load cell so it's difficult to hold them exactly steady other than at 0 or 100%. And I'm reacting as soon as I hear the prompts, I'm guessing it's just thd delay in processing then getting to my ears.

On track and ready to drive

## 2026-11-03

okay, I'm now on Laguna Seca in a Lotus Esprit vs the Nurburgring in a Ford GT40 - what next? I have the game paused

okay, I saved the game, closed the app, started the app, loaded the save state and will start driving again

let's move on - SimHub is ready and listening on port 8000, can we use the same Forza Horizon format we've been using? If so let's go - I'll leave the game pasued until you tell me we're ready for the next test

continue

looks like we're getting data but we're only getting engine and gears - no impacts, curbs, road-feel, off-road, wheel slip, etc - have we just not done that yet? If you need me to do more driving just tell me

continue

yup, great - all seems to be working with the only issue now that it's not stopping the data when I pause the game so I'm getting a steady buzz from whatever was the state when I paused, if we can fix that great - next let's move on to display as the game is rendering a 16:9 window instead the main PCSX2 window rather than rendering 32:9/16:9 - I'd like this to be an option in the GUI tool that this is going to be built into, in the GT4 profile so it's one of the options that can be set. When you're ready for me to test let me know, closing the app now

continue

put the EXE in Downlaods

you have triples at 48:9 - but mine is actually 64:9 (10240X1440) so can you update?

I understand the issue - 32:9 is fine, unless you're saying you can get the HUD to work properly at 64:9, if you can great then do so otherwise just skip it - I also see "Guided capture" in the app, scanner, etc. - that's not necessary for an end user, right? If that's all unnecessary now let's remove it - at least from GT4 since we have data working now.

suggest a name - I'm thinking SimBinder (as I can get simbinder.com) but since this isn't only going to be for sims.... any other ideas that might be avilable as a domain?

Thanks - I went with GameTether.com - good idea.

Now, let's get back to 32:9, etc  - seems to work great in game but the UI is menus is stretched, assume you cant change taht - if you can add pillarbars great, if not I get it. But now it looks pretty awful, are there things I can see for AA, etc. to make it look much better as I've seen that on Youtube at 4k, etc. We don't need to code that - unless it is a setting for this specific game then it would make sense

I'm sorry - I'm in the wrong session, THIS app is going to be called "SimEffects Telemetry", (GameTether is another app)

I'm testing and suddenly data isn't working - the display is 32:9 so thats great but while I see that PINE is connected and telemetry output is running I'm getting "Waiting for Data" in Simhub with a green light so I think the connection is there just no data flowing. I'm still in game paused but am doing to exit my copy of the app so you can test - let me know what you need

I think you're wrong - I now have no copies running, I start one, start GTTelemetry.exe and it's the same - everything is closed right now, so?

I was driving the whole time you weren't getting any data - does this mean our memory locations have changed? I'm in another different car/track - so should I still drive? What to do next?

I AM NOT DRIVING - I"M WAITING FOR YOU TO TELL ME WHAT TO DO

yup, it's working - have we fixed it for good or do I need to test more?

random question - no code now, just exploring - could we add FFB/telemetry/wheel/pedals/etc to this project: [Release DCport 0.5.4 · qnfn/Driveclub-Port](https://github.com/qnfn/Driveclub-Port/releases/tag/windows-v0.5.4)

give me an updated version of this in Downloads

Going forward we're going to call this "SimEffects Telemetry"

It seems that we have curbs wrong - when I run over a curb I see "Impacts" lighting up and nothing on the Curbs channel - it's possible it's due to the way you detected what I was doing, you told me to drive on curbs but gave me very little time to find them - as remember there aren't all that many curbs so we need to do it like this - when you're ready for me to drive on curbs I'll find one on the left then as soon as I run over it (and I'm ON the curb) I'll pause so you have a point to know where I was on the curb for say 1 second before I paused it, if you think that will work let's do it - tell me when to drive and again it'll take me time to find a curb so either w'll use this pause method or if you have something better let me know.

okay I first paused on the right curb then drove a bit and paused on the left curb last

Working great - now let's move on to FOV and seating position - I've closed SET (SimEffects Telemetry) and the PCXS2 so let me know what you need to get this done

ready - I'm in game sitting on the straight with my foot on the brake to hold the car still and I'm in the interior/cockpit view (definitly want to be able to change things on the hood view too) - tell me what you need next

okay - I switched cameras - what next

continue

unpaused in hood view

1. I have no numpad on my keyboard so do NOT use those keys
2. sounds right
3. give me new exe and commit
