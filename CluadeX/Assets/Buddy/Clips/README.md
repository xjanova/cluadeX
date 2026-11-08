# น้อง CluadeX — her clips

Short clips of her moving, played in the big corner frame (`Views/CluadexBuddy.Clips.cs` picks them, `CluadexBuddy.Away.cs`
calls out when the owner is away; `Effects/ChromaKey.fx` takes the green out). Owner 2026-11-08: "ใช้ digen สร้างวีดีโออารมณ์
… 30+", then "ทำไปเยอะๆ เลย สัก 100 คลิป … ทั้งมีเสียงไม่มีเสียง", "เรียกได้ ฮัลโหล มีใครอยู่มั้ย", "เสร็จแล้วค่ะ ทำหลายๆ แบบ",
"ต้องตัดสินใจ", "ร้องไห้ เสียใจ งอน", "เซ็ง เบื่อ หาว".

## How they were made

- **Start pictures:** her still (`../cluadex-hero.png`'s source) on flat green `#00B140`, 720×1280. For clips with something in
  her hands (iPad, coffee mug, snack, headphones, book, pillow, magnifying glass, phone) and asleep, ChatGPT edited that picture
  ("start from the ORIGINAL attached picture … same woman, outfit, framing, size and position (hips and skirt where they are),
  flat green") and the edit was put back on the original's framing with a similarity transform from ORB features on her skirt
  and waist, so every clip starts where the still is. `../cluadex-hero-sleep.png` is cut from the asleep picture the same way.
- **Digen** (digen.ai/create). The first clips: **Real Motion 2.6**, 1080P, no sound. The rest: **Real Motion 3.5 (Audio)**,
  480P, 5 s, with sound. Every prompt ends with *"Static camera, no zoom, no pan, she stays in the same position and size. The
  background stays plain flat solid green the whole time."* and then one of:
  - *sound* — "Audio: only soft, cute, non-verbal sounds from her matching what she does; no words, no speech, no singing
    lyrics, no music, no background noise."
  - *says* — "She speaks in clear English with a sweet, bright, playful young woman's voice, lips in sync: "…" She says only
    that line. No music, no background noise."
  - *quiet* / loops — "Audio: quiet, no music, no speech."
- **Cut and squeezed** (Python + OpenCV + ffmpeg): frame 0 dropped; every frame mapped back onto frame 0 with a similarity
  transform from ORB features on her skirt and waist, smoothed (when she leans or turns so far that the tracking is lost —
  a scale over 20 % or a shift over a fifth of the width — the camera is kept as Digen made it); cut to a 1:2 box that holds
  nearly the whole frame, hat to skirt (x 30, y 8, 420×840 of the 480-wide frame; owner: "ขยายให้เห็นเธอทั้งคลิป"), the stills
  `../cluadex-hero*.png` cut the same, and scaled to **240×480**; RM 2.6 (16 fps)
  interpolated to 24 fps, RM 3.5 is 24 fps already; loops (`pp`) played forward then back, no sound; H.264 CRF 32
  `-tune animation`, `+faststart`. One-shots with sound keep it: loudness-normalised (`loudnorm I=-20`), mono 44.1 kHz AAC 48k.
- **Checked by eye**, 8 frames of each: sleep clips keep her eyes shut; nothing green is in her hands (an iPad screen turned to
  the camera showed green and was dropped — the key would cut a hole in it).

Each clip is about 100–250 KB. The app plays them at up to 150×300, on a raised plate (MainWindow.xaml `BuddyPlate`). With the sound setting off (Settings › General › Appearance) every
clip is muted and she does not pick the ones where she speaks.

## The first clips (RM 2.6, no sound)

| Scene | Clip | s | Prompt (before the shared ending) |
|---|---|---|---|
| idle (loop) | `idle` | 10 pp | breathes calmly, blinks slowly twice, soft gentle smile, head sways very slightly |
| idle extra | `idle-look` | 5 | glances slowly left, then right, looks back with a soft smile |
| idle extra | `idle-hat` | 5 | adjusts her cat-ear beanie with one hand |
| idle extra | `idle-hair` | 5 | tucks a strand of hair behind her ear |
| idle extra | `idle-stretch` | 5 | rolls her shoulders, stretches her neck side to side |
| idle extra | `idle-hum` | 10 | hums a tune, sways her head to the rhythm |
| idle extra | `idle-wink` | 5 | a quick playful wink |
| idle extra | `idle-peek` | 5 | leans forward to peek at a screen below, leans back |
| working (loop) | `working` | 10 pp | looks down as if reading code, nods a little |
| working (loop) | `working-type` | 10 pp | types on a keyboard below the frame, glances up |
| working (loop) | `thinking` | 10 pp | finger on her chin, looks up and to the side, a thoughtful nod |
| working extra | `thinking-hmm` | 5 | taps her chin, squints, "hmm" |
| working extra | `working-aha` | 5 | eyes widen, finger up — "aha!" |
| waiting (loop) | `waiting` | 5 pp | looks at you expectantly, raised eyebrows, head tilt |
| waiting extra | `waiting-wave` | 5 | a small wave to get your attention |
| waiting extra | `waiting-tap` | 5 | taps her cheek patiently |
| listening (owner typing) | `listening` | 5 pp | listens, nods slowly |
| listening | `listening-lean` | 5 pp | leans in, hand behind her ear, nods |
| happy (reaction) | `happy` | 5 | lights up, big smile, happy little bounce |
| happy | `happy-clap` | 5 | claps her hands |
| happy | `happy-peace` | 5 | peace sign and a wink |
| happy | `happy-heart` | 5 | finger heart |
| sad (reaction) | `sad` | 5 | smile fades, small sigh, sorry pout |
| sad | `sad-sorry` | 5 | palms together, a sorry bow |
| sad | `sad-oops` | 5 | "oops" — hand over mouth, sheepish smile |
| sleep | `yawn` | 5 | covers a yawn, eyes droop |
| sleep (loop) | `asleep-nod` | 5 pp | dozes, head nods (sleep picture) |
| waking | `wake` | 5 | opens her eyes, rubs one, sleepy smile (sleep picture) |
| CluadeX opens | `greet` | 5 | waves hello |
| CluadeX opens | `greet-wai` | 5 | a Thai wai and a small bow |
| a tip bubble | `suggest` | 5 | finger up — "I have a tip" |
| a tip bubble | `suggest-wink` | 5 | finger near her lips, a secret-tip wink |

## RM 3.5 clips (with sound)

| Clip | Start picture | Kind | Prompt (before the shared ending) |
|---|---|---|---|
| `happy-giggle` | her picture | sound | giggles cutely, covering her mouth with her fingertips, eyes squeezed happily. |
| `happy-jump` | her picture | sound | does a happy little bounce on the spot with both fists up and a cheerful "yay" sound. |
| `happy-dance` | her picture | sound | does a small cute happy dance, swaying her shoulders and hips side to side, humming happily. |
| `happy-proud` | her picture | sound | puts both hands on her hips and nods proudly with a confident little "hmph!" and a smug smile. |
| `happy-thumbs` | her picture | sound | gives the viewer a thumbs up and a playful wink with a cheerful little "mm!". |
| `happy-cheer` | her picture | sound | raises both fists in the air and cheers excitedly, beaming. |
| `laugh` | her picture | sound | laughs heartily at something funny, shoulders shaking, eyes crinkled. |
| `cheer-done` | her picture | sound | celebrates a finished job: she throws both hands up happily, then claps twice, beaming. |
| `relief` | her picture | sound | wipes her forehead with the back of her hand and lets out a relieved "phew", smiling. |
| `sad-sniff` | her picture | sound | pouts sadly, her eyes get teary, she sniffles cutely. |
| `sad-droop` | her picture | sound | lowers her head and shoulders in disappointment with a small sad sigh. |
| `annoyed` | her picture | sound | is irritated: she rolls her eyes and lets out an annoyed huff, lips pressed together. |
| `annoyed-sigh` | her picture | sound | is fed up: she rubs her temple with two fingers and lets out a long tired sigh. |
| `annoyed-tap` | her picture | sound | crosses her arms and taps her fingers on her arm impatiently, a small "tsk" sound, unimpressed look. |
| `angry` | her picture | sound | is cutely angry: she puffs out her cheeks, frowns and stamps her foot, small fists clenched. |
| `angry-fume` | her picture | sound | is fuming in a cute way: fists clenched and shaking, frowning hard, a little "grrr" sound. |
| `angry-cross` | her picture | sound | crosses her arms, turns her head away sharply with an offended "hmph!", eyes closed. |
| `surprised` | her picture | sound | gasps in surprise, both hands flying to her cheeks, eyes wide. |
| `oops` | her picture | sound | realizes a mistake: eyes go wide, she bites her lip and gives a nervous "uh-oh" look. |
| `confused` | her picture | sound | is confused: she tilts her head, scratches the side of her beanie, a questioning "hm?". |
| `shy` | her picture | sound | blushes shyly, covers her face with both hands, then peeks out between her fingers giggling softly. |
| `sneeze` | her picture | sound | does a small cute sneeze into her elbow, then sniffs and smiles sheepishly. |
| `hiccup` | her picture | sound | hiccups cutely twice, surprised each time, then covers her mouth and giggles. |
| `stretch-big` | her picture | sound | does a big satisfying stretch with both arms up over her head and a cute "mmmh~" sound, then relaxes. |
| `yawn-big` | her picture | sound | gives a big cute yawn, covering her mouth with her hand, eyes watering slightly, then blinks sleepily. |
| `sleepy-blink` | her picture | sound | is very sleepy: her eyes slowly close, her head nods down, then she jolts awake with a tiny startled sound and shakes her head. |
| `greet-morning` | her picture | sound | greets the morning: a little stretch, then a cheerful wave to the viewer with a bright happy hum. |
| `greet-evening` | her picture | quiet | gives the viewer a gentle relaxed wave and a soft warm smile. |
| `welcome-back` | her picture | sound | spots the viewer coming back, lights up and waves excitedly with both hands, bouncing a little. |
| `idle-sway` | her picture | sound | sways gently side to side humming a soft tune to herself, eyes half closed. |
| `idle-nails` | her picture | quiet | casually looks at her fingernails, then glances back up at the viewer with a small smile. |
| `idle-earring` | her picture | quiet | idly plays with her butterfly earring with her fingertips, looking to the side. |
| `idle-bored` | her picture | sound | is bored: she puffs her cheeks, lets the air out slowly with a little sigh, and looks around. |
| `idle-lookaround` | her picture | quiet | looks around curiously, up and to the left, then to the right, then back with a smile. |
| `idle-ears` | her picture | quiet | pats the cat ears of her beanie with both hands and smiles playfully. |
| `idle-heart` | her picture | quiet | raises both arms above her head and makes a big heart shape, smiling. |
| `idle-wiggle` | her picture | quiet | does a cute little wiggle of her shoulders and smiles at the viewer. |
| `waiting-lean` | her picture | sound | leans toward the viewer and tilts her head, a questioning "hmm~?" sound, eyebrows raised. |
| `waiting-knock` | her picture | sound | knocks gently on the screen glass in front of her with her knuckles, then waits with a hopeful smile. |
| `waiting-watch` | her picture | quiet | looks at her wrist as if checking a watch, then back at the viewer, eyebrows raised. |
| `listening-hum` | her picture | sound | listens attentively and nods slowly with soft "mm-hm" sounds, looking at the viewer. |
| `thinking-tap` | her picture | sound | thinks: she looks up, taps her chin with one finger and makes a long thoughtful "hmmm~". |
| `thinking-snap` | her picture | sound | suddenly gets an idea and snaps her fingers with an excited "ah!", pointing up. |
| `suggest-point` | her picture | quiet | smiles and points up and to her left as if showing a note floating next to her. |
| `suggest-whisper` | her picture | sound | cups a hand beside her mouth and leans in as if telling a secret, a soft "psst" sound. |
| `nod-yes` | her picture | quiet | nods yes happily twice with a bright smile. |
| `shake-no` | her picture | quiet | shakes her head no and wags her index finger playfully. |
| `salute` | her picture | sound | gives a playful salute with a perky little "hup!" and a wink. |
| `focus` | her picture | sound | gets serious and determined: she pushes up her sleeves, nods to herself with a small "hup". |
| `nervous` | her picture | sound | fidgets nervously, twiddling her fingers, with an awkward little laugh. |
| `love` | her picture | sound | puts both hands over her heart and sighs dreamily, eyes sparkling. |
| `blow-kiss` | her picture | sound | blows a kiss to the viewer with a soft "mwah" and a wink. |
| `shrug` | her picture | sound | shrugs with both palms up and a cute "eh?" expression. |
| `cry` | her picture | sound | starts crying cutely: her lower lip trembles, big tears roll down her cheeks, she sobs softly and rubs her eyes with her sweater sleeves. |
| `cry-wipe` | her picture | sound | has teary eyes, sniffles, wipes her tears with the back of her hand, then gives a small brave smile. |
| `sulk` | her picture | sound | sulks: she puffs out her cheeks, pouts, crosses her arms and turns her face away with a small "hmph", then glances back from the corner of her eye. |
| `sulk-peek` | her picture | sound | is sulking, arms crossed and face turned away, then peeks back at the viewer to see if they noticed, and quickly looks away again pouting. |
| `bored-chin` | her picture | sound | is bored: she rests her chin on one hand, taps her cheek with a finger and lets out a long bored sigh, eyes half closed. |
| `bored-slump` | her picture | sound | slumps her shoulders and lets her arms hang loosely, tilting her head back with a long drawn-out "uuugh", totally bored. |
| `meh` | her picture | sound | makes a flat unimpressed face, eyes half-lidded, mouth sideways, and a little "meh" sound with a tiny shrug. |
| `yawn-stretch` | her picture | sound | yawns widely and stretches both arms up at the same time with a cute sleepy "hnnngh~", then lowers them and smacks her lips. |
| `yawn-small` | her picture | sound | does a small quick yawn, pats her mouth with her fingertips, then blinks and smiles sheepishly. |
| `facepalm` | her picture | sound | puts her palm on her face and sighs, then peeks through her fingers. |
| `away-hello` | her picture | says | "Hello? Is anyone there?" — leans toward the screen, looks around and calls out curiously, one hand cupped by her mouth. |
| `away-hellooo` | her picture | says | "Hellooo~? Where did you go?" — looks left and right searching for the viewer, calling out in a sing-song way. |
| `away-knock` | her picture | says | "Knock knock! Anybody home?" — knocks gently on the screen glass in front of her and tilts her head, smiling. |
| `away-lonely` | her picture | says | "Hey... I'm getting a little lonely." — pouts a little, fiddles with her hair, glancing at the viewer. |
| `away-still-here` | her picture | says | "I'm still here, you know!" — waves both hands to get attention, cheerful but a tiny bit sulky. |
| `away-miss` | her picture | says | "Come back soon, okay?" — smiles softly and gives a small wave, a little wistful. |
| `away-boo` | her picture | says | "Psst... are you asleep too?" — leans in close and whispers playfully, then giggles. |
| `done-alldone` | her picture | says | "All done!" — beams and throws both hands up happily. |
| `done-tada` | her picture | says | "Ta-da! It's ready!" — presents with both open hands like a magician, very proud. |
| `done-finished` | her picture | says | "Finished! Take a look!" — points down toward the screen below her with an excited smile. |
| `done-mission` | her picture | says | "Mission complete!" — gives a playful salute and a wink. |
| `done-yay` | her picture | says | "Yay, we did it!" — claps her hands and bounces happily. |
| `done-yours` | her picture | says | "All yours - it's done!" — gives a thumbs up and a warm smile. |
| `done-fun` | her picture | says | "Done! That was fun." — stretches her arms happily and smiles, satisfied. |
| `done-easy` | her picture | says | "Easy peasy!" — brushes her hands together and grins confidently. |
| `decide-call` | her picture | says | "Your call, boss!" — points at the viewer with both index fingers and smiles. |
| `decide-goahead` | her picture | says | "Should I go ahead?" — tilts her head and looks at the viewer questioningly, hands clasped. |
| `decide-which` | her picture | says | "Which one do you want?" — holds up one hand, then the other, as if weighing two choices, curious face. |
| `decide-check` | her picture | says | "Can you check this for me?" — leans forward with a hopeful, slightly pleading look. |
| `decide-ok` | her picture | says | "I need your OK on this one!" — raises a hand like a student asking, eager smile. |
| `decide-waiting` | her picture | says | "I'm waiting for you~" — rests her chin on her hands and sways a little, smiling patiently. |
| `err-oops` | her picture | says | "Oops... something went wrong." — winces and scratches her cheek sheepishly. |
| `err-retry` | her picture | says | "Sorry! Let me try again." — bows her head a little with palms together, then nods determinedly. |
| `err-again` | her picture | says | "Ugh, not again!" — throws her head back in frustration, then pouts. |
| `err-seriously` | her picture | says | "Seriously?!" — crosses her arms with an exasperated cute frown. |
| `hi-there` | her picture | says | "Hi there!" — waves cheerfully at the viewer with a big smile. |
| `hi-morning` | her picture | says | "Good morning!" — stretches a little, then waves with a bright sunny smile. |
| `hi-evening` | her picture | says | "Good evening!" — gives a relaxed wave and a warm soft smile. |
| `hi-back` | her picture | says | "Welcome back!" — lights up and waves excitedly with both hands. |
| `sulk-back` | her picture | says | "Hmph! You left me all alone!" — pouts with puffed cheeks and crossed arms, sulky but cute, glancing at the viewer. |
| `away-bored` | her picture | says | "I'm sooo bored... come play with me!" — rests her chin on her hand, sighs and pouts, then looks at the viewer hopefully. |
| `night-late` | her picture | says | "It's getting late... rest soon, okay?" — yawns a little, rubs one eye and smiles sleepily. |
| `working-ipad` | iPad | loop pp | works on the iPad in her hands, looking down at it, tapping and swiping the screen calmly, small nods. |
| `busy` | iPad | sound | is very busy: she taps and swipes the iPad screen quickly, frowns in concentration, a frazzled little huff. |
| `ipad-read` | iPad | loop pp | reads on the iPad, scrolling slowly with one finger, nodding slightly. |
| `ipad-surprise` | iPad | sound | reads on the iPad, then her eyes widen in surprise with a small "oh!" and she leans closer. |
| `ipad-giggle` | iPad | sound | reads something funny on the iPad and giggles softly. |
| `ipad-frown` | iPad | sound | reads on the iPad and frowns with a puzzled "hmm...". |
| `ipad-type` | iPad | sound | types very fast on the iPad with both thumbs, soft tapping sounds, concentrating. |
| `ipad-sigh` | iPad | sound | looks at the iPad, sighs at how much work there is, then keeps tapping. |
| `coffee` | coffee mug | sound | takes a slow sip from the mug and lets out a contented "ahh", eyes closed. |
| `coffee-blow` | coffee mug | sound | blows gently on the hot coffee, then takes a careful sip. |
| `coffee-hot` | coffee mug | sound | sips the coffee, it is too hot, she pulls back with a cute "ah, ah!" and fans her tongue. |
| `coffee-cozy` | coffee mug | quiet | holds the warm mug close with both hands, eyes closed, swaying gently, cozy. |
| `coffee-cheers` | coffee mug | sound | raises the mug toward the viewer as a cheers with a cheerful little "mm!", then sips. |
| `asleep` | asleep | loop pp | sleeps peacefully standing, eyes closed the whole time, breathing slowly and deeply, head tilted a little, otherwise completely still. |
| `asleep-mumble` | asleep | sound | sleeps standing with her eyes completely shut the whole video, she mumbles softly and smiles in her dream. She never opens her eyes. |
| `asleep-snore` | asleep | sound | sleeps standing with her eyes completely shut the whole video, a tiny cute soft snore, her head sways slowly. She never opens her eyes. |
| `snack` | snack | sound | takes a bite of the donut and chews happily with a delighted "mmm!". |
| `snack-offer` | snack | quiet | holds the donut out toward the viewer as if offering to share, smiling sweetly. |
| `snack-look` | snack | sound | looks at the donut with sparkling eyes, licks her lips, then takes a small bite. |
| `music-bob` | headphones | quiet | listens to music on her headphones, eyes closed, bobbing her head to the beat. |
| `music-hum` | headphones | sound | listens to music on her headphones and hums along softly, swaying. |
| `music-dance` | headphones | quiet | listens to music on her headphones and dances a little with her shoulders, smiling. |
| `book-read` | book | loop pp | reads the open book, her eyes moving along the lines, a small focused nod. |
| `book-flip` | book | sound | turns a page of the book with a soft paper sound and keeps reading. |
| `book-aha` | book | sound | reads, finds the answer, taps the page with an excited "ah!" and looks up smiling. |
| `pillow-hug` | pillow | sound | hugs the pillow, rubs her cheek on it and gives a sleepy little yawn. |
| `pillow-doze` | pillow | loop pp | hugs the pillow and dozes off with her eyes closed, head resting on it, breathing slowly. |
| `search` | magnifying glass | quiet | looks through the magnifying glass, moving it slowly left and right, searching carefully. |
| `search-found` | magnifying glass | sound | looks through the magnifying glass, spots something and says a delighted "ah-ha!", pointing. |
| `phone-check` | phone | quiet | checks the phone in her hand, reads a message and smiles. |
| `phone-buzz` | phone | sound |  in her hand buzzes, she looks at it surprised with a little "oh?", then smiles. |

`pp` = ping-pong loop (twice as long on disk). Which scene plays which clip, and when she speaks, is in
`Views/CluadexBuddy.Clips.cs`. A clip that is missing or fails leaves her on the still pictures — none of them is needed for
CluadeX to work. themetest `--hero` checks every name the code plays is here, small, and that nothing else is.
