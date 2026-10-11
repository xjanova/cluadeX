namespace CluadeX.Services.Pet;

/// <summary>When the pet says something.</summary>
public enum PetMoment
{
    Hello, Idle, Poked, PokedALot, Patted, PickedUp, Dropped, BuildFailed, BuildPassed, TurnDone, Error,
    LateNight, LongSession, Morning, FallAsleep, WakeUp, Hatched, EggPoked, EggIdle, EggAlmost, OnTaskbar, OnWindow, PutAway,
}

/// <summary>
/// What the pet says, in Thai or English, by personality. A personality only overrides the moments where it would
/// sound different; everything else comes from the shared lines. "{name}" is the pet's name.
/// </summary>
public static class PetLines
{
    static readonly Dictionary<PetMoment, (string[] th, string[] en)> Shared = new()
    {
        [PetMoment.Hello] = (["มาแล้ว! วันนี้ทำอะไรกันดี", "ฮัลโหล~ คิดถึงนะ", "เปิดเครื่องแล้ว ลุยกัน!"],
                             ["I'm here! What are we making today?", "Hiii~ missed you", "Back again? Let's go!"]),
        [PetMoment.Idle] = (["ฮัมเพลงเบา ๆ ♪", "เดินเล่นแป๊บนะ", "*มองซ้ายมองขวา*", "โค้ดเยอะจัง…", "หิวขนมอะ"],
                            ["*hums a little tune*", "Just going for a walk", "*looks around*", "So much code…", "I want a snack"]),
        [PetMoment.Poked] = (["อุ๊ย!", "จิ้มทำไมอะ~", "เฮ้!", "ว่าไง?"], ["Eep!", "Why the poke~", "Hey!", "Yes?"]),
        [PetMoment.PokedALot] = (["พอแล้ววว!", "จิ้มอีกทีจะงอนนะ", "เจ็บนะเนี่ย!"], ["Stooop!", "One more poke and I'm sulking", "Ow!"]),
        [PetMoment.Patted] = (["ชอบ~ ♥", "อีกที อีกที!", "อุ่นจัง ♥"], ["I like that ♥", "Again, again!", "So warm ♥"]),
        [PetMoment.PickedUp] = (["ว้ายยย", "จะพาไปไหน!", "สูงจัง!"], ["Whoaaa", "Where are we going?!", "So high!"]),
        [PetMoment.Dropped] = (["ตุ้บ!", "ลงจอดสวย ๆ", "เกือบไปแล้ว"], ["Bonk!", "Nailed the landing", "That was close"]),
        [PetMoment.BuildFailed] = (["build พังอีกแล้ว…", "แดงเถือกเลย", "ไม่เป็นไร ลองใหม่"], ["Build broke again…", "So much red", "It's fine, try again"]),
        [PetMoment.BuildPassed] = (["ผ่าน! เก่งมาก", "เขียวปี๋!", "เย้ build ผ่าน"], ["Passed! Nice", "All green!", "Yay, it builds"]),
        [PetMoment.TurnDone] = (["เสร็จแล้วนะ~", "งานเสร็จ ตรวจหน่อย", "เรียบร้อย!"], ["All done~", "Finished — have a look", "Done!"]),
        [PetMoment.Error] = (["มีอะไรผิดปกตินะ", "เอ๊ะ error", "อุ๊ย พังตรงไหน"], ["Something went wrong", "Uh oh, an error", "Oops, what broke"]),
        [PetMoment.LateNight] = (["ดึกแล้วนะ ไปนอนเถอะ", "ตาจะปิดแล้ว…", "พรุ่งนี้ค่อยทำต่อไหม"], ["It's late, go to bed", "My eyes are closing…", "Finish tomorrow?"]),
        [PetMoment.LongSession] = (["พักสายตาหน่อย", "ลุกไปยืดเส้นบ้างนะ", "ดื่มน้ำด้วยนะ"], ["Rest your eyes a bit", "Go stretch your legs", "Drink some water"]),
        [PetMoment.Morning] = (["อรุณสวัสดิ์!", "เช้าแล้ว กาแฟยัง?", "วันใหม่ ไฟแรง!"], ["Good morning!", "Morning — coffee yet?", "New day, full power!"]),
        [PetMoment.FallAsleep] = (["ง่วง… zzz", "งีบแป๊บนะ", "zzZ"], ["Sleepy… zzz", "Quick nap", "zzZ"]),
        [PetMoment.WakeUp] = (["ตื่นแล้ว!", "หลับไปนานไหมเนี่ย", "หาวว~"], ["I'm up!", "Did I sleep long?", "*yaaawn*"]),
        [PetMoment.Hatched] = (["สวัสดี! เราชื่อ {name}", "ออกมาแล้ว! {name} มาแล้ว", "ในที่สุด! ฝากตัวด้วยนะ"],
                               ["Hello! I'm {name}", "I hatched! {name} is here", "Finally! Nice to meet you"]),
        [PetMoment.EggPoked] = (["*ไข่ขยับ*", "*ก๊อก ก๊อก*", "ยังไม่ถึงเวลา…"], ["*the egg wiggles*", "*tap tap*", "Not yet…"]),
        [PetMoment.EggIdle] = (["*ไข่โยกเบา ๆ*", "…", "*มีอะไรขยับข้างใน*"], ["*the egg rocks gently*", "…", "*something moves inside*"]),
        [PetMoment.EggAlmost] = (["*ไข่สั่นแรงขึ้น!*", "ใกล้แล้ว…", "*แกร๊ก*"], ["*the egg shakes harder!*", "Almost…", "*crack*"]),
        [PetMoment.OnTaskbar] = (["ขอนั่งตรงนี้นะ", "ทาสก์บาร์อุ่นดี"], ["I'll sit here", "The taskbar is warm"]),
        [PetMoment.OnWindow] = (["วิวสวยจากบนนี้", "ขอเกาะหน้าต่างหน่อย"], ["Nice view up here", "Mind if I sit on your window?"]),
        [PetMoment.PutAway] = (["บ๊ายบาย เรียกได้นะ", "ไปพักก่อนนะ"], ["Bye-bye, call me back anytime", "Going to rest"]),
    };

    static readonly Dictionary<PetPersonality, Dictionary<PetMoment, (string[] th, string[] en)>> ByPersonality = new()
    {
        [PetPersonality.Grumpy] = new()
        {
            [PetMoment.Hello] = (["มาอีกแล้วเหรอ", "ฮึ่ม… สวัสดี"], ["You again", "Hmph… hi"]),
            [PetMoment.Idle] = (["เบื่อ", "ทำไมโค้ดยาวงี้", "อย่ามายุ่ง"], ["Bored", "Why is this code so long", "Don't bother me"]),
            [PetMoment.Poked] = (["อะไร!", "อย่าจิ้ม", "ฮึ่ม!"], ["What!", "Don't poke", "Hmph!"]),
            [PetMoment.PokedALot] = (["จิ้มอีกทีกัดนะ!", "หยุด! เดี๋ยวนี้!"], ["Poke me again and I bite!", "Stop. Now!"]),
            [PetMoment.BuildFailed] = (["บอกแล้ว", "พังเหมือนเดิม", "ใครเขียนเนี่ย… อ้อ เธอ"], ["Told you", "Broken. As usual", "Who wrote this… oh, you"]),
            [PetMoment.BuildPassed] = (["ก็พอใช้", "ฟลุ๊กมั้ง"], ["Acceptable", "Lucky, probably"]),
        },
        [PetPersonality.Sassy] = new()
        {
            [PetMoment.Idle] = (["เขียนโค้ดสวยกว่านี้ได้นะ~", "ชิว ๆ", "อย่าลืม commit ล่ะ"], ["You could write prettier code~", "Chillin'", "Don't forget to commit"]),
            [PetMoment.Poked] = (["จิ้มเก่งนะ", "ว่างเหรอจ๊ะ"], ["Great poking skills", "Bored, are we?"]),
            [PetMoment.BuildFailed] = (["ว้าว แดงทั้งจอ ศิลปะมาก", "อีกรอบไหมคะ"], ["Wow, all red. Very artistic", "Another round?"]),
            [PetMoment.BuildPassed] = (["เห็นไหม ทำได้ถ้าตั้งใจ", "ปรบมือ~"], ["See? You can when you try", "*slow clap*"]),
            [PetMoment.LateNight] = (["ดึกขนาดนี้ bug ก็หลับแล้วนะ"], ["Even the bugs are asleep by now"]),
        },
        [PetPersonality.Shy] = new()
        {
            [PetMoment.Hello] = (["อ…อรุณสวัสดิ์", "*แอบมอง*"], ["H-hi…", "*peeks*"]),
            [PetMoment.Idle] = (["*แอบอยู่ตรงนี้*", "…"], ["*hiding here*", "…"]),
            [PetMoment.Poked] = (["ว๊าย…", "*หน้าแดง*"], ["Eep…", "*blushes*"]),
            [PetMoment.Patted] = (["*เขิน*", "ข…ขอบคุณ"], ["*flustered*", "Th-thank you"]),
        },
        [PetPersonality.Sleepy] = new()
        {
            [PetMoment.Idle] = (["หาวว…", "ขอนอนอีก 5 นาที", "zz…"], ["*yaaawn*", "Five more minutes", "zz…"]),
            [PetMoment.Poked] = (["อือ…", "ปลุกทำไม…"], ["Mmh…", "Why wake me…"]),
            [PetMoment.LateNight] = (["นอนด้วยกันเถอะ…", "zzz… เธอก็ไปนอนนะ"], ["Let's both sleep…", "zzz… you sleep too"]),
        },
        [PetPersonality.Hyper] = new()
        {
            [PetMoment.Hello] = (["มาแล้วววว!!", "ลุยยย!"], ["HELLOOO!!", "LET'S GOOO!"]),
            [PetMoment.Idle] = (["วิ่งเล่นดีกว่า!", "เร็ว ๆ ๆ!", "ฮึบ ฮึบ!"], ["Zoomies!", "Faster faster!", "Hup hup!"]),
            [PetMoment.Poked] = (["เล่นด้วย! เล่นด้วย!", "อีก!"], ["Play with me!", "Again!"]),
            [PetMoment.BuildPassed] = (["เย้ยยย!!", "เก่งที่สุดในโลก!"], ["YAAAY!!", "Best in the world!"]),
        },
        [PetPersonality.Caring] = new()
        {
            [PetMoment.Idle] = (["เหนื่อยไหม", "เป็นกำลังใจให้นะ", "กินข้าวหรือยัง"], ["Tired?", "I'm rooting for you", "Have you eaten?"]),
            [PetMoment.BuildFailed] = (["ไม่เป็นไรนะ ค่อย ๆ แก้", "พักแป๊บแล้วค่อยลองใหม่"], ["It's okay, one fix at a time", "Take a breath, then try again"]),
            [PetMoment.LongSession] = (["ทำงานนานแล้ว พักหน่อยนะ", "ห่วงนะ ลุกไปเดินบ้าง"], ["You've been at it a while — rest a bit", "I worry — go take a walk"]),
        },
        [PetPersonality.Nerdy] = new()
        {
            [PetMoment.Idle] = (["O(n log n) สวยดี", "เทสครบหรือยัง", "รู้ไหม git มี bisect"], ["O(n log n) is pretty", "Tests all written?", "Did you know git has bisect?"]),
            [PetMoment.BuildFailed] = (["ลองอ่าน error บรรทัดแรกก่อน", "น่าจะ null นะ"], ["Read the first error first", "Smells like a null"]),
            [PetMoment.BuildPassed] = (["compile ผ่าน แต่เทสล่ะ?"], ["It compiles. And the tests?"]),
        },
        [PetPersonality.Cheerful] = new()
        {
            [PetMoment.Idle] = (["วันนี้อากาศดี~", "ยิ้มหน่อย!", "ทำได้อยู่แล้ว!"], ["Lovely day~", "Smile!", "You've got this!"]),
            [PetMoment.BuildFailed] = (["ไม่เป็นไร! ครั้งหน้าผ่านแน่", "สู้ ๆ!"], ["No worries! Next one passes", "You can do it!"]),
        },
    };

    /// <summary>A line for this moment; <paramref name="pick"/> chooses among the candidates.</summary>
    public static string Say(PetMoment moment, PetPersonality? personality, bool thai, string name, int pick)
    {
        (string[] th, string[] en) set;
        if (personality is { } p && ByPersonality.TryGetValue(p, out var own) && own.TryGetValue(moment, out var mine)) set = mine;
        else if (!Shared.TryGetValue(moment, out set)) return "";
        var lines = thai ? set.th : set.en;
        if (lines.Length == 0) return "";
        return lines[(int)((uint)pick % (uint)lines.Length)].Replace("{name}", name);
    }

    /// <summary>The personality's name, for the pet's card.</summary>
    public static string PersonalityName(PetPersonality p, bool thai) => (p, thai) switch
    {
        (PetPersonality.Cheerful, true) => "ร่าเริง", (PetPersonality.Cheerful, false) => "Cheerful",
        (PetPersonality.Grumpy, true) => "ขี้หงุดหงิด", (PetPersonality.Grumpy, false) => "Grumpy",
        (PetPersonality.Sassy, true) => "ปากแซ่บ", (PetPersonality.Sassy, false) => "Sassy",
        (PetPersonality.Shy, true) => "ขี้อาย", (PetPersonality.Shy, false) => "Shy",
        (PetPersonality.Sleepy, true) => "ขี้เซา", (PetPersonality.Sleepy, false) => "Sleepy",
        (PetPersonality.Hyper, true) => "ไฮเปอร์", (PetPersonality.Hyper, false) => "Hyper",
        (PetPersonality.Caring, true) => "ขี้ห่วง", (PetPersonality.Caring, false) => "Caring",
        (PetPersonality.Nerdy, true) => "เนิร์ด", (PetPersonality.Nerdy, false) => "Nerdy",
        _ => p.ToString(),
    };

    public static string SpeciesName(PetSpecies s, bool thai) => (s, thai) switch
    {
        (PetSpecies.Cat, true) => "แมว", (PetSpecies.Bunny, true) => "กระต่าย", (PetSpecies.Fox, true) => "จิ้งจอก",
        (PetSpecies.Dragon, true) => "มังกรน้อย", (PetSpecies.Slime, true) => "สไลม์", (PetSpecies.Ghost, true) => "ผีน้อย",
        (PetSpecies.Owl, true) => "นกฮูก", (PetSpecies.Robot, true) => "หุ่นยนต์", (PetSpecies.Fairy, true) => "นางฟ้า",
        (PetSpecies.Girl, true) => "เด็กผู้หญิง", (PetSpecies.Penguin, true) => "เพนกวิน", (PetSpecies.Frog, true) => "กบ",
        (PetSpecies.Dragon, false) => "Little dragon", (PetSpecies.Ghost, false) => "Little ghost",
        _ => s.ToString(),
    };
}
