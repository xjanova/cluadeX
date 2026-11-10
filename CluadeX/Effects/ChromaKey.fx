// น้อง CluadeX's clips are filmed on flat green (Assets/Buddy/Clips, README.md there). This takes the green out, pulls
// the green spill off her hair and skin, and fades her edges the way the still picture is faded (cluadex-hero.png:
// bottom 20 %, sides 10 %, top 3 %), so a clip and the still look the same at their edges.
//
// Rebuild after an edit (Windows SDK):
//   fxc /T ps_2_0 /E main /O3 /Fo ChromaKey.ps ChromaKey.fx
// The compiled ChromaKey.ps is committed: the build does not need the SDK.

sampler2D input : register(s0);

// x: how green a pixel must be (green minus the larger of red and blue) to start going see-through
// y: how much more green before it is gone
// z: how far green may stand above red/blue once the key has passed (spill)
// w: the choke — a neighbour this much greener than the key's start pulls the pixel out
float4 key : register(c0);

// x: bottom fade, y: side fade, z: top fade — each a share of the side
float4 fade : register(c1);

// x, y: how far the choke looks — four pixels of the video (4/240, 4/480): Windows' video decoder blurs her edge into
// the green over 3-4 pixels, with the start picture's white halo just inside
float4 texel : register(c2);

// how green a pixel is: green over the larger of red and blue
float greenness(float3 c)
{
    return c.g - max(c.r, c.b);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 c = tex2D(input, uv);
    // WPF hands the shader premultiplied colour; the video is opaque, so c.a is 1 and c.rgb is the colour itself.
    float g0 = greenness(c.rgb);
    // choke: where lavender meets green the mixed pixel is not green enough to key and reads as a grey-green rim; a pixel
    // beside the green is keyed as green as its greenest neighbour, so her edge comes in by up to four video pixels
    float gn = max(max(greenness(tex2D(input, uv + float2(texel.x, 0)).rgb), greenness(tex2D(input, uv - float2(texel.x, 0)).rgb)),
                   max(greenness(tex2D(input, uv + float2(0, texel.y)).rgb), greenness(tex2D(input, uv - float2(0, texel.y)).rgb)));
    // a0: how much of this pixel is her by its own colour; a: also pulled out beside the green (the choke)
    // (both keyed in one float2 to fit ps_2_0's 64 slots)
    float2 k = 1 - smoothstep(key.xx, key.xx + key.yy, float2(g0, gn - key.w));
    float a0 = k.x;
    float a = min(a0, k.y);
    // unmix: a see-through edge pixel (a hair strand) is her colour times a0 plus the green times (1 - a0); take the green
    // share out (the clips' green, as Windows decodes it, measures 0.12, 0.69, 0.29). Only the pixel's own green share: taking the choke's share out
    // too leaves a magenta rim where a lavender pixel was choked but was never green.
    float3 p = saturate(c.rgb - (1 - a0) * float3(0.12, 0.69, 0.29));
    // spill: no pixel left greener than the middle of its red and blue
    p.g = min(p.g, (p.r + p.b) * 0.5 + key.z * a0);
    // p is premultiplied by a0; the choke takes it down to a
    p *= a / max(a0, 0.02);
    float3 e = saturate(float3(1 - uv.y, min(uv.x, 1 - uv.x), uv.y) / fade.xyz);
    e = e * e * (3 - 2 * e);
    float edge = e.x * e.y * e.z * c.a;
    return float4(p * edge, a * edge);
}
