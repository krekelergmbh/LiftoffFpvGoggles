// Composite video emulation for Liftoff FPV Goggles.
//
// This does not paint artefacts on top of the picture. It encodes the image into a single
// composite signal the way an analog transmitter does - luma plus a colour subcarrier - adds
// noise to that signal, and decodes it again. Dot crawl, rainbow patterns on fine detail,
// sideways colour smear and colour dying before the picture does are not written anywhere in
// here. They fall out of doing the real thing badly, which is exactly what the hardware does.
//
// Built as an AssetBundle against Unity 2022.3, the version Liftoff ships. Deliberately free of
// any Post Processing Stack include: the vertex setup below is all that StdLib would have
// provided, and not depending on the package keeps the bundle buildable from an empty project.

Shader "Hidden/FpvGoggles/CompositeVideo"
{
    HLSLINCLUDE

    #include "UnityCG.cginc"

    // Handles both stereo modes on its own: a plain sampler2D in multi-pass, a texture array in
    // single-pass instanced. Sampling through the macro means never finding out which one a
    // given headset ended up using.
    UNITY_DECLARE_SCREENSPACE_TEXTURE(_MainTex);

    float _Subcarrier;      // colour subcarrier cycles across one line
    float _Lines;           // lines in the emulated signal
    float _Noise;           // amplitude of the noise mixed into the composite signal
    float _Saturation;      // gain on the decoded chroma
    float _ChromaBleed;     // 0 = chroma as sharp as luma, 1 = fully smeared
    float _Jitter;          // horizontal instability, in fractions of a line
    float _Softness;        // 0 = full luma detail, 1 = averaged over the filter window
    float _Seed;            // changes per frame, so nothing stands still

    struct Attributes
    {
        float3 vertex : POSITION;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct Varyings
    {
        float4 position : SV_POSITION;
        float2 uv : TEXCOORD0;
        UNITY_VERTEX_OUTPUT_STEREO
    };

    // The stack draws a single oversized triangle whose positions are already in clip space,
    // so this only has to pass them through and derive the UV from them.
    Varyings Vert(Attributes input)
    {
        Varyings output;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

        output.position = float4(input.vertex.xy, 0.0, 1.0);
        output.uv = (input.vertex.xy + 1.0) * 0.5;

    #if UNITY_UV_STARTS_AT_TOP
        output.uv = output.uv * float2(1.0, -1.0) + float2(0.0, 1.0);
    #endif

        return output;
    }

    // NTSC colour space. Luma in x, the two colour difference channels in yz - which is the
    // whole point, because only those two get thrown away by a weak signal.
    float3 RgbToYiq(float3 c)
    {
        return float3(
            dot(c, float3(0.299, 0.587, 0.114)),
            dot(c, float3(0.5959, -0.2746, -0.3213)),
            dot(c, float3(0.2115, -0.5227, 0.3112)));
    }

    float3 YiqToRgb(float3 c)
    {
        return float3(
            dot(c, float3(1.0, 0.956, 0.619)),
            dot(c, float3(1.0, -0.272, -0.647)),
            dot(c, float3(1.0, -1.106, 1.703)));
    }

    float Hash(float2 p)
    {
        p = frac(p * float2(443.897, 441.423));
        p += dot(p, p.yx + 19.19);
        return frac((p.x + p.y) * p.x);
    }

    // The half-cycle step per line is what makes the pattern crawl instead of standing still,
    // and it is the reason real dot crawl moves the way it does.
    //
    // Not called 'line': that is a reserved word in HLSL, and the error it produces points at
    // the line after the one that is wrong.
    float Phase(float x, float lineIndex)
    {
        return 6.2831853 * (_Subcarrier * x + 0.5 * lineIndex + _Seed);
    }

    #define TAPS 8

    float4 FragComposite(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

        float2 uv = input.uv;
        float lineIndex = floor(uv.y * _Lines);

        // Lines do not all start in the same place when the signal is poor. This is what tears
        // the picture sideways, and it costs one hash.
        float jitter = (Hash(float2(lineIndex, _Seed * 977.0)) - 0.5) * _Jitter;

        // Four samples per subcarrier cycle, which is the spacing the decoding below assumes.
        float step = 1.0 / max(1.0, _Subcarrier * 4.0);

        float3 taps[TAPS];
        float xs[TAPS];

        [unroll]
        for (int i = 0; i < TAPS; i++)
        {
            xs[i] = uv.x + (i - (TAPS - 1) * 0.5) * step + jitter;
            taps[i] = RgbToYiq(UNITY_SAMPLE_SCREENSPACE_TEXTURE(_MainTex, float2(xs[i], uv.y)).rgb);
        }

        float lumaSoft = 0.0;
        float2 chroma = 0.0;
        float lumaWeight = 0.0;
        float chromaWeight = 0.0;

        [unroll]
        for (int j = 0; j < TAPS; j++)
        {
            // A real encoder band-limits the colour before it puts it on the subcarrier - it
            // has to, the channel has no room for sharp colour. Skipping that was the mistake:
            // full detail colour modulated onto the carrier lands straight back in the
            // brightness when it is decoded again, one subcarrier period away from where it
            // belongs. That is the doubling, and it is fixed here rather than hidden later.
            float2 iq = 0.0;
            [unroll]
            for (int k = -1; k <= 1; k++) iq += taps[clamp(j + k, 0, TAPS - 1)].yz;
            iq /= 3.0;

            float phase = Phase(xs[j], lineIndex);
            float carrierCos = cos(phase);
            float carrierSin = sin(phase);

            // Encode: one wire carrying brightness with the colour riding on a subcarrier.
            float signal = taps[j].x + iq.x * carrierCos + iq.y * carrierSin;

            // Noise goes into the signal itself, not onto the finished picture. That single
            // decision is what makes weak reception look right: the decoder below turns some of
            // it into snow and some of it into coloured blotches, and enough of it drowns the
            // subcarrier so the colour disappears while the picture is still readable.
            signal += (Hash(float2(xs[j] * 1024.0, lineIndex + _Seed * 331.0)) - 0.5) * _Noise;

            // Brightness survives on a narrow, centre-weighted window; colour is averaged over
            // the full span. That difference is the bandwidth a composite signal actually gives
            // each of them, and it is where the sideways colour smear comes from.
            float t = (j + 0.5) / TAPS;
            float centre = 0.5 - 0.5 * cos(6.2831853 * t);
            float wideWeight = lerp(centre, 1.0, saturate(_ChromaBleed));

            lumaSoft += signal * centre;
            lumaWeight += centre;

            chroma += float2(signal * carrierCos, signal * carrierSin) * wideWeight;
            chromaWeight += wideWeight;
        }

        lumaSoft /= max(1e-4, lumaWeight);
        chroma = chroma * 2.0 / max(1e-4, chromaWeight);

        // Averaging the signal is the obvious way to strip the subcarrier back out of the
        // brightness, and it is why the first version was so soft: that window is a dozen
        // pixels wide. A real receiver does not do this. It subtracts the colour it has just
        // decoded from the untouched signal, which leaves the brightness at full detail - and
        // leaves an error exactly where the colour estimate was wrong, which is where dot crawl
        // comes from in the first place.
        float centreX = uv.x + jitter;
        float3 centreYiq = RgbToYiq(UNITY_SAMPLE_SCREENSPACE_TEXTURE(_MainTex, float2(centreX, uv.y)).rgb);
        float centrePhase = Phase(centreX, lineIndex);
        float centreCos = cos(centrePhase);
        float centreSin = sin(centrePhase);

        // Encoded with the same band-limited colour as every other sample. Encoding this one at
        // full colour detail and then subtracting a band-limited estimate from it would leave
        // the difference behind as an edge echo - the very thing being fixed above.
        float2 centreIq = (taps[TAPS / 2 - 1].yz + taps[TAPS / 2].yz) * 0.5;
        float centreSignal = centreYiq.x + centreIq.x * centreCos + centreIq.y * centreSin;
        centreSignal += (Hash(float2(centreX * 1024.0, lineIndex + _Seed * 331.0)) - 0.5) * _Noise;

        float lumaSharp = centreSignal - (chroma.x * centreCos + chroma.y * centreSin);

        float luma = lerp(lumaSharp, lumaSoft, saturate(_Softness));

        float3 decoded = YiqToRgb(float3(luma, chroma * _Saturation));
        return float4(max(0.0, decoded), 1.0);
    }

    // Plain copy, used to get in and out of the reduced resolution the signal is emulated at.
    float4 FragCopy(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        return UNITY_SAMPLE_SCREENSPACE_TEXTURE(_MainTex, input.uv);
    }

    ENDHLSL

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "Composite"
            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragComposite
                #pragma target 3.0
            ENDHLSL
        }

        Pass
        {
            Name "Copy"
            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragCopy
                #pragma target 3.0
            ENDHLSL
        }
    }

    Fallback Off
}
