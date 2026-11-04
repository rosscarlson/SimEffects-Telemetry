# Requests

What was asked, word for word, by date.

## 2026-11-02

# Project Brief: Gran Turismo Telemetry

## Locations
- Working directory: C:\Users\ross\OneDrive\Code\Windows\SimEffectsTelemetry
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

remove 48:9 and 64:9 options in the Display drop down - then remove the text below it and the text at the bottom of "Camera". Also where we can set the bindings for each of the seat movements should also be able to be a joystick button, not just a keyboard key(s).

continue

I now have RPCS3 running with Gran Turismo 6 playing right now - FFB/bindings are working now we need to do what we did for GT4, let's start with telemetry and do it like last time, you tell me what to do when - remembering that curbs specifically are really difficult so we should do them where I run over one then pause the game and you look for the pause and read before it. I'm ready when you are, talk to me baby

stopped and paused

41mph in 2nd gear

60 mph in 3rd gear (was braking to that speed as I overshot a bit)

you want me to do them all in order and tell you when I"m done? I do NOT have a clutch pedal nor can I shift right now as the game is set to auto-shifting so I'll just have to get to whatever speed is needed for 4th gear.

Tell me when to start

okay, I sorta did step 2 twice as the first time I didn't see the note about turning the wheel, so I then did that but I was hitting a wall so not really moving - then I ended paused in 4th gear at 90mph

okay did left curb pause, right curb pause, left curb pause, the grass pause

continue

continue

continue

okay drove around a bit and paused on a curb on the right wheels/side

continue

great, now let's move on to seating position and FOV, what do you need? Le'ts use the same bindings block for ALL games so don't store that under the "Game" block but in it's own block as all games will have those bindings - and if one doesn't support them we'll gray them out. I have the game paused but closed SET let me know what you need next

Okay, I'm ready to start - tell me when you're recording

paused in cockpit view now

fully stopped, revs are around 3500 and I'm in cockpit view

I'm on hood now and paused

paused in cockpit view with the car stopped and foot on the brake

running

nope, I cycled back to the cockpit view and no change - if we can't make the seat move what about FOV, that's more important to me anyway but both would be nice, POV is the priority though

the game crashed inside the emulator

so for FOV are we patching RPCS3 (or is that the best method) or adding our own here? Seems the right aswer to to patch it, right? Don't code, just answer

so then would SET have a button to apply that patch for the user? agian only answer, be brief

but you'd be able to "install" it once and it would stick, right?

and likelihood of success? BE BRIEF

I have the game running, check the version only

okay - let's table this for now and move on to data in GT5, I'm going to launch that so we can do the same detection process to find the telemetry as that's most important. Let me know when you're ready to start testing

if you're recording now - stop - the game is still getting setup, I'll tell you when I'm ready for you to start watching

great, in GT5 at Laguna Seca in a Lexus, paused doing 48mph in 2nd gear goin straight with my feet off the pedals, what next

stopped with foot on brake 100% and paused

70mph in 3rd gear and paused

okay I did 1, 2, 3 then I drove a bit, braking and accelerating, then I paused twice on curbs on the right tires then ended paused on a curb on the left tires. When I was at half/full throttle with the wheel turned since I'm on a track I was hitting the wall and off track but I did drive with those inputs.

Next

Continue, but realize that I am now not at the computer. So if you need input from me you need to stop and wait for me to tell you that I've returned and then you can pick back up your work

Continue, but realize that I am now not at the computer. So if you need input from me you need to stop and wait for me to tell you that I've returned and then you can pick back up your work

Continue, but realize that I am now not at the computer. So if you need input from me you need to stop and wait for me to tell you that I've returned and then you can pick back up your work

Continue, but realize that I am now not at the computer. So if you need input from me you need to stop and wait for me to tell you that I've returned and then you can pick back up your work

COntinue - I'm at my computer but not in game - let me know what you need as we got cut off

great - yup, seems to work - now what about 32:9? Look this one up too and make sure there isn't already a solution we can tap into

can this help? [distrobox-gaming/docs/gt5-master-mod.md at master · akitaonrails/distrobox-gaming](https://github.com/akitaonrails/distrobox-gaming/blob/master/docs/gt5-master-mod.md)

you say I'm on 1.04 - can you handle this and download/upgrade for me?

closed

1.05 installed but I had to interven

[image 1]I found the attached, is this what we want? Enable Aspect Ratio I assume? What else?

[image 1] I still can't get it to update - how do I use the files you gave me?

[image 1] can you download those for me too? I'll install but you download (I'm going to move the other files you downloaed BTW)

can you just let the download run and ignore it, not wasting time checking on it again as I see where it's going and can take care of it when it's done. If you CAN do that then proceed - if you must wait or tha'ts a problem stop and alert me.

If you can continue:

Here's a project that we're going to merge into this one so it's all in one app

https://github.com/rosscarlson/Azimuth-Telemetry

we created this specifically for the games that are in Azimuth launcher, which right now is FH2 and FH3 (with a specific version of Azimuth) - we need to pull in the code for the Forza games from here and put them in our wrapper. We then can remove Forza Motorsport 4 and FH1 as we built that into the launchers for the projects so it doesn't make sense to put those here. So when you bring this over it should look exactly the same as it does in AzimuthTelemetry including the wheel controls/bindings. Let's do add the same output and telemetry visuals we have now by merging the two interfaces.

Let me know if you need anything or when you're ready.

You can merge Forza Horizon 2 and 3 - they both use the exact same code and get rid of the "Start Azimuth" checkbox. Remove text "Sends Forza Data Out Packets....." then get rid of the "Probe drive" sectoin completely, that was for testing.  

Let's move the "Start" button (and just say "Start" with the icon) in the upper right of the Game block that is in the upper left with it being a bit smaller to fit. Remove "Per-car profiles" from all but FM4, in fact only show what's planned for GT5 GT6 and remove the rest

now we need to add an auto-update system, let's add a new block in the bottom left that handles that, showing the user when an udpate is avilable and giving them an "Update" button, showing what version they are, what's current and the number of releases behind they are. Let's call this version 0.2 and publish to github - that way once I install (give me an installer) I can test the updater when you push 0.3 - the installer should write to Program Files\SimEffect\Telemetry

## 2026-11-04

Write me a brief I can give to another instance of Claude Code so it can update my website with this project. I want it to be somewhat brief in what the page has, less is more sorta thing, and give some details on how to get each game working with it - I'll flush out the rest of the page myself.

put the text here for me so I can copy/paste

First - I renamed this working directory as SimEffectsTelemetry - so anywhere we had any other name or path please update now

we have somehow broken GT6 - when I launch I get an error that the PS3 game has lilkely crashed and it certainly seems to - your fault? Was working

well it keeps happening and I can't skip anything - but this WAS working as we did a lot of testing, if you can do something to help do as Im' stuck now

okay now I can launch the game but as soon as I get on track I see nothing - the hud works but nothing in my view does regardless of the view I select - so I think something you did while trying to update the seating position may have caused it - either way thoughts?

okay, but what version do we need for 32:9?

no, we found one for "Aspect Ratio"

okay, it's working - but I hvae an issue in both GT5 and GT6, my shifters don't work. I've triple chcked them in RPCS3 under the G27 emulation and it shows them both set and registering under shifters but they don't work in either game. Seems other buttons do so not sure what's up - they work fine in everything else (the paddles) - other than that - and a performance issue I haven't started on yet, I'm getting 32fps but there's a lot of audible static and performance is awful - nothing else big is running. Thoughts

1. I'm a pro-level instrcutor, it is NOT that (I set it to manual)
2. I see no options to change any bindings in game

and I only mean my sequential paddles - look this up online please

great - but now a bigger issue, SimEffects Telemetry is no longer working with 6 (haven't tried 5 yet) - I have it running but it just shows "Not driving" while I am, let me know what you need me to do - I'm going to close my copy of the app, I'm paused in GT6 on track alone right now

continue

do you remember where we were and my last command

I am paused on track with only SimHub open to get the data from you/our app - let's go

yup, that fixed it - so does that mean we can only support specific versions of the game

okay, so in short we need to get me on the latest version of the game right? Since I had trouble earlier how far should I go now? Try half the updates, see if it works and find where it breaks?

I want 2 things - share with others and 32:9 - in that order - I'm updating now as I have them all downloaded. I'll try to see where it breaks - but can you look up the black/no track issue I had with 2.11?

okay I'm now stuck in a loop - the game crashed during the updates now I get an error that the update can't install and it reboots - and over and over - I tried updating to 116 (I was on 115) but that didn't help - so I guess put me back to stock

should I do 5 at a time, launch game and check or just all at once?

suddenly Alt-Enter won't make RPCS3 go to non-full screen, I see the cursor again and I can alt tab but if I click back on the game it's still actually fullscreen and goes back to fullscreen - am I missing some setting? I have the game running but it sure seems like it's died again as it's just frozen doing the update

great, put me back to stock, I'll update to 1.05, we'll support that and then if that works you can start on 32:9 support

okay, I have the game playing again and will go get on track after I send this message, I do NOT have telemetry open, do what you need, goal is to test it, confirm it, then move on to 32:9

okay, let's fix the version we have published then an update as 0.3

great, thanks working - next up is 32:9 - what do you need from me? I'm in game paused - I also want to add 21:9 since that's so popular but we'll stop with those 3 - if we can add this to the app so others can "apply" it to their game that's the goal, if not let me know

sorry, I stopped you to compact first....

Here's what I asked of you before I stopped you:

great, thanks working - next up is 32:9 - what do you need from me? I'm in game paused - I also want to add 21:9 since that's so popular but we'll stop with those 3 - if we can add this to the app so others can "apply" it to their game that's the goal, if not let me know

okay, in car, on the hood view, not paused, not touching controls

I saw something change very quickly

no, no you haven't - unless I need to restart. When I go fullscreen, which I am now, it only renders the center 16:9, now squished - should I restart or?

I'm on track, not paused, holding the brake to hold the car still

well it sorta worked - the HUD is correct and it's filling my 32:9 monitor but there's a HORRIBLE strobing/flashing/flickering - like once every 1-2 seconds or so, it's like the screen blanks then comes back, etc. - unplayable obviously - thoughts

where are you fixing this, our app or RCPS3?

working great now - no flicker. I've closed my copy of SET and RPCS3

did you update my local EXE in program files?

just publish as 0.3.1 and I'll use the auto-updater, I have confidence you got it right ;-)
