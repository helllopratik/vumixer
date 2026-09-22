/*
 * vimeter.c - tiny calibrated PipeWire level-meter helper.
 *
 * Captures one PipeWire node (usually a virtual-sink *.monitor) with a
 * native pw_stream, computes RMS/peak per channel and prints lines to
 * stdout for the VU mixer UI to consume:
 *
 *     M <label> <rmsL> <rmsR> <peakL> <peakR> <clip>   (clip=0/1)
 *
 * Usage: vimeter <node-target> <label>
 *
 * Build: gcc vimeter.c -o vimeter $(pkg-config --cflags --libs libpipewire-0.3) -lm
 *        (fallback: -I<include dirs> -lpipewire-0.3 -lm)
 *
 * The connection strategy mirrors pw-cat: node.target property +
 * AUTOCONNECT + an explicit F32/48k/stereo format pod. Only PipeWire
 * is needed; there is no PortAudio, no polling of the graph, and levels
 * are the real sample values (0.0-1.0).
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <time.h>
#include <pipewire/pipewire.h>
#include <spa/param/audio/format-utils.h>
#include <spa/param/audio/raw.h>

#define CHANNELS 2

struct meter_data {
    struct pw_main_loop *loop;
    struct pw_stream *stream;
    char label[64];
    float rms[CHANNELS];
    float peak[CHANNELS];
    int clip;
    double last_emit;
};

static double monotonic(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double) ts.tv_sec + (double) ts.tv_nsec / 1e9;
}

static void emit_report(struct meter_data *d)
{
    printf("M %s %.4f %.4f %.4f %.4f %d\n",
           d->label,
           d->rms[0], d->rms[1],
           d->peak[0], d->peak[1],
           d->clip ? 1 : 0);
    fflush(stdout);
}

static int debug_enabled(void)
{
    static int cached = -1;
    if (cached < 0)
        cached = getenv("VIMETER_DEBUG") != NULL ? 1 : 0;
    return cached;
}

static void on_process(void *userdata)
{
    struct meter_data *d = (struct meter_data *) userdata;
    static unsigned long long pcalls, psuc, pdeq;
    pcalls++;
    if (debug_enabled() && pcalls % 200 == 0)
        fprintf(stderr, "pcalls=%llu deq_ok=%llu proc=%llu\n",
                pcalls, pdeq, psuc);

    struct pw_buffer *b = pw_stream_dequeue_buffer(d->stream);
    if (!b) {
        pdeq++;
        return;
    }

    struct spa_buffer *sb = b->buffer;
    if (sb->n_datas >= 1) {
        struct spa_data *dat = &sb->datas[0];
        if (dat->data && dat->chunk) {
            int stride = dat->chunk->stride;
            size_t size = dat->chunk->size;
            if (stride <= 0)
                stride = 4;              /* F32 */
            if (size > 0 && stride > 0) {
                int n = (int) (size / stride);
                if (n > 0) {
                    const float *s = (const float *) dat->data;
                    double sums[CHANNELS] = { 0.0, 0.0 };
                    float pks[CHANNELS] = { 0.0f, 0.0f };

                    for (int i = 0; i < n; i++) {
                        int c = i % CHANNELS;
                        float v = s[i];
                        sums[c] += (double) v * v;
                        float a = fabsf(v);
                        if (a > pks[c])
                            pks[c] = a;
                        if (a >= 0.999f)
                            d->clip = 1;
                    }
                    int half = n / CHANNELS;
                    for (int c = 0; c < CHANNELS; c++) {
                        double r = half > 0 ? sqrt(sums[c] / half) : 0.0;
                        d->rms[c] = (float) (r * 0.65 + d->rms[c] * 0.35);
                        d->peak[c] = pks[c];
                    }
                    psuc++;
                }
            }
        }
    }

    /* a capture stream must return the buffer to the pool, otherwise it
     * starves after the first round (only one cycle would ever run) */
    pw_stream_queue_buffer(d->stream, b);

    double now = monotonic();
    if (now - d->last_emit >= 0.05) {
        d->last_emit = now;
        emit_report(d);
    }
}

static void on_state_changed(void *userdata, enum pw_stream_state old,
                             enum pw_stream_state state, const char *error)
{
    if (debug_enabled())
        fprintf(stderr, "STATE %d -> %d%s%s\n", old, state,
                error ? " err=" : "", error ? error : "");
}

static int run(uint64_t target_id, const char *label)
{
    struct meter_data data;
    memset(&data, 0, sizeof(data));
    snprintf(data.label, sizeof(data.label), "%s", label);

    pw_init(NULL, NULL);

    data.loop = pw_main_loop_new(NULL);
    if (!data.loop) {
        fprintf(stderr, "FATAL: no main loop\n");
        return 1;
    }
    struct pw_context *context = pw_context_new(
        pw_main_loop_get_loop(data.loop), NULL, 0);
    struct pw_core *core = pw_context_connect(context, NULL, 0);
    if (!core) {
        fprintf(stderr, "FATAL: cannot connect to PipeWire\n");
        return 1;
    }

    struct pw_properties *props = pw_properties_new(NULL, NULL);
    pw_properties_set(props, PW_KEY_NODE_NAME, "vimeter-stream");
    pw_properties_set(props, PW_KEY_MEDIA_TYPE, "Audio");
    pw_properties_set(props, PW_KEY_MEDIA_CATEGORY, "Capture");
    pw_properties_set(props, PW_KEY_MEDIA_ROLE, "Monitor");
    pw_properties_set(props, PW_KEY_NODE_LATENCY, "512/48000");

    data.stream = pw_stream_new(core, "vimeter", props);
    if (!data.stream) {
        fprintf(stderr, "FATAL: cannot create stream\n");
        return 1;
    }

    static struct spa_hook stream_listener;
    struct pw_stream_events events;
    memset(&events, 0, sizeof(events));
    events.version = PW_VERSION_STREAM_EVENTS;
    events.process = on_process;
    events.state_changed = on_state_changed;
    pw_stream_add_listener(data.stream, &stream_listener, &events, &data);

    uint8_t buffer[1024];
    struct spa_pod_builder b = SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
    const struct spa_pod *params[1];
    params[0] = spa_format_audio_raw_build(&b, SPA_PARAM_EnumFormat,
        &SPA_AUDIO_INFO_RAW_INIT(
            .format = SPA_AUDIO_FORMAT_F32,
            .rate = 48000,
            .channels = CHANNELS,
            .position = { SPA_AUDIO_CHANNEL_FL, SPA_AUDIO_CHANNEL_FR }));

    int flags = PW_STREAM_FLAG_AUTOCONNECT |
                PW_STREAM_FLAG_MAP_BUFFERS;
    if (getenv("VIMETER_NO_RT") == NULL)
        flags |= PW_STREAM_FLAG_RT_PROCESS;
    if (pw_stream_connect(data.stream, PW_DIRECTION_INPUT, target_id,
                          flags, params, 1) < 0) {
        fprintf(stderr, "FATAL: stream connect failed\n");
        return 1;
    }

    fprintf(stderr, "READY %s\n", label);
    fflush(stderr);
    pw_main_loop_run(data.loop);

    pw_stream_destroy(data.stream);
    return 0;
}

int main(int argc, char *argv[])
{
    if (argc < 3) {
        fprintf(stderr, "usage: %s <node-id> <label>\n", argv[0]);
        return 2;
    }
    char *end;
    uint64_t id = strtoull(argv[1], &end, 10);
    if (end == argv[1] || *end != '\0') {
        fprintf(stderr, "error: <%s> is not a numeric node id\n", argv[1]);
        return 2;
    }
    return run(id, argv[2]);
}