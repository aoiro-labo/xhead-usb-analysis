# XHEAD-STUDIO / mnservice gRPC protocol — reconstructed schema

This directory contains a clean-room reconstruction of the local gRPC wire
protocol spoken between the XHEAD-STUDIO GUI and its background service,
recovered from the decompiled .NET client library (`mnClientDotNet`,
ilspycmd output). The `.proto` files under [`proto/`](proto/) are a
schema-only reconstruction — message/field/enum names and numbers, i.e. the
interface shape needed for wire compatibility — with no vendor
implementation logic copied in.

**How this was derived, and why it's unusually exact:** each
`mnFramework.grpc/Ms*Reflection.cs` file embeds, as a base64 string, the
*actual compiled `FileDescriptorProto`* that `protoc` produced from
Micomsoft's original `.proto` source — i.e. it's not just C# reflecting on
itself, it's a serialized copy of the compiler's own parse of the original
schema text (field names, field numbers, types, oneofs, enum values,
nesting — all of it, byte-exact). This was decoded with Python's
`google.protobuf` library rather than inferred from IL/C# property names,
which is why field numbers, types, and even original field-name casing in
these files should be treated as verified ground truth rather than
best-effort inference.

## 1. Two-process architecture

```
xhead_studio.exe (GUI, .NET/WinForms)
        |  gRPC, insecure/plaintext, localhost:50051
        |  service: msBroadcastService  (see ms_service.proto)
        v
mnservice.exe (background service, native C++)
        |  FFmpeg for decode; Pegasys TMPGEnc SDK for encode
        |  libusbK for device I/O
        v
XHEAD-USB (hardware) --USB--> ISDB-T/OFDM modulation --RF (coax)-->
```

- The GUI process is a thin gRPC client: `mnClientDotNet.dll`
  (`mnFramework`, `mnFramework.grpc`) connects to `localhost:50051` at
  startup and drives everything through the 6 RPCs below.
- `mnservice.exe` is the actual gRPC *server*. It owns the USB/hardware
  connection; the raw USB protocol between `mnservice.exe` and the
  XHEAD-USB device itself is **not** covered by this reconstruction (that
  would require native binary analysis / USB capture, out of scope here).
- Nothing in this schema is XHEAD-specific at its core — see
  "Surprising findings" below for evidence this is a shared, general
  broadcast-pipeline framework with XHEAD as one selectable hardware
  backend.

## 2. File organization

The 9 files below mirror the *actual* original `.proto` file boundaries
(recovered directly from each `FileDescriptorProto.name` /
`.dependency` list — this is proven, not a guessed grouping) rather than
an invented split. No file groups messages that weren't already grouped
together by Micomsoft.

| File | Deps | Enums | Top-level messages | Contents |
|---|---|---|---|---|
| `ms_base.proto` | — | 17 | 13 (+ nested) | shared A/V format types, generic media-content/program-stream tree, control-param envelope, firmware file metadata, storage-path tree, system info |
| `ms_property.proto` | — | 4 | 14 (+ nested) | the generic property/descriptor/range/variant system (see §4) + the config-file document format |
| `ms_output.proto` | base, property | 0 | 1 | output devices (RF modulator / generic IO) |
| `ms_capture.proto` | base, property | 0 | 1 | frame/packet capture objects |
| `ms_engine.proto` | base, property | 1 | 4 | codec engine objects + declared capacities |
| `ms_channel.proto` | base, property | 0 | 3 | broadcast channel + program objects |
| `ms_source.proto` | base, property | 3 | 6 (+ nested) | media source objects (URL/capture/transcode/resample) |
| `ms_event.proto` | base, property, output, capture, engine, channel, source | 1 | 4 | server-push event envelope |
| `ms_service.proto` | base, property, output, capture, engine, channel, source, event | 2 | 4 | the RPC service, session object, request/response envelope |

**Totals: 9 files, 28 enums, 50 top-level messages** (61 including nested
message/enum types).

## 3. Wire-compatibility notes — read before regenerating code

These are deliberate departures from a literal "add `package
msbroadcast;`" instruction, made in favor of the stated top-level goal
(a third party being able to implement a working client):

1. **No `package` is declared, on purpose.** The real compiled schema has
   an *empty* `package` field in every one of the 9 `FileDescriptorProto`s
   (verified directly, not assumed). If you add `package msbroadcast;`,
   `protoc`/`grpc` tooling will compute the RPC path as
   `/msbroadcast.msBroadcastService/sendRequest` instead of the real
   `/msBroadcastService/sendRequest`, and your generated stub will fail to
   reach `mnservice.exe`. Keep these files package-less if you want an
   out-of-the-box working client; only add a package if you also
   reconfigure your stub's method path accordingly.
2. **`option allow_alias = true;`** is required (and present) on 3 enums
   because the original schema really does define two names for the same
   number: `msResult` (`ResultFail` / `ResultFailUnknown` both `32768`),
   `msFrameStructure` (`Interlaced` / `FieldTopBottom` both `1`), and
   `msEventID` (`EventServiceStart` / `EventServicePing` both `0`). Without
   `allow_alias`, `protoc` will refuse to compile these files.
3. **Field-name casing correction vs. earlier manual notes:** field names
   in this reconstruction are **PascalCase** (`ClientID`, `HandleID`,
   `Cmd`, oneof `Param`, etc.), taken verbatim from the decoded
   `FileDescriptorProto` bytes. An earlier manual read of the generated C#
   (`WriteRawTag`/property names) had assumed lowerCamelCase (`clientID`,
   `cmd`, `param`) — that was a reasonable guess since C#'s protoc plugin
   always force-capitalizes a field's first letter, making the transform
   look plausible, but the decoded original source text shows the real
   names already start uppercase. This has **zero effect on wire bytes**
   (only field *numbers* affect the wire); it only matters if you care
   about matching Micomsoft's exact source names.
4. **Two genuine casing inconsistencies survive in the original schema
   itself** (not an artifact of this reconstruction — both come directly
   from the decoded source text): `msControlParam.strParam` is lowercase
   while its oneof siblings `UintParam`/`IntParam`/`BufParam` are not; and
   `msMediaContent.Index` (outer, uppercase) vs.
   `msMediaContent.Stream.index` (nested, lowercase). Harmless, but a nice
   confirmation that these names are literal source text, not something
   this reconstruction invented.
5. **Field-number gaps** were found and preserved as comments (not
   `reserved` statements, since the compiled descriptors carry no explicit
   `reserved` ranges — we don't want to assert retirement semantics we
   can't prove): `msSystem` is missing 5 and 7; `msOutput` is missing 4;
   `msPropertyField` is missing 45; `msSourceParam`'s oneof is missing 11.
   Likely fields removed during schema evolution.

## 4. The 6 RPC methods

```proto
service msBroadcastService {
  rpc connectService     (msRequest) returns (msResponse);          // unary
  rpc subscribeService   (msRequest) returns (stream msEvent);      // server-streaming
  rpc unsubscribeService (msRequest) returns (msResponse);          // unary
  rpc disconnectService  (msRequest) returns (msResponse);          // unary
  rpc sendRequest        (msRequest) returns (msResponse);          // unary
  rpc sendControl        (msRequest) returns (stream msEvent);      // server-streaming
}
```

- **connectService** — session handshake. Send `msRequest{Cmd:
  CmdConnect, Param.Client: msClientParam{Privilege, Name}}`. The server
  assigns a session id and replies with `msResponse.Param.Client`, a full
  `msClient` snapshot: your new `HandleID` (used as `msRequest.ClientID`
  in every subsequent call), the granted `Privlege`, host `System` info,
  `Storage` tree, the persisted `Config` (`msConfigFile`), and — usefully
  for discovery — the complete list of **already-existing** `Engines`,
  `Captures`, `Channels`, `Sources`, `Outputs` (each with their live
  `Properties`, see §5). This is where you'd first see the RF-modulation
  `msOutput` object.
- **subscribeService** — opens a long-lived server-streaming feed of
  `msEvent` (channel/source/capture status changes, storage add/del,
  service ping, debug events — see the `msEventID` table below).
- **unsubscribeService** / **disconnectService** — session teardown
  counterparts to subscribe/connect.
- **sendRequest** — the general-purpose command channel; carries every
  `msServiceCmd` except `CmdControl` (open/close/start/stop objects,
  program add/commit, apply-config, etc.).
- **sendControl** — a second, *streaming*, vendor/device-specific control
  channel. Carries `msRequest{Cmd: CmdControl, Param.Control:
  msControlParam}` and streams back `msEvent`s reporting progress/result
  (see `EventControlProgress`/`EventControlFirmware`/`EventControlResult`
  below). This is the channel used for opaque device operations —
  **firmware updates being the clearest example**: `msControlParam` +
  `msFirmwareFile`/`msFWUsbConfig` + the `EventControlFirmware` /
  `EventControlProgress` / `EventControlResult` event IDs are all wired
  together for exactly this purpose.

### `msServiceCmd` → object/oneof pairing (inferred from naming + shape)

`msRequest.Param` is a single oneof with only 6 branches (`Index`,
`Client`, `Source`, `Content`, `Channel`, `Control`) shared across every
command below — there is **no** dedicated `Capture` or `Engine` branch,
which is itself notable (see §6).

| Cmd | Value | Likely `msRequest.Param` branch | Notes |
|---|---|---|---|
| `CmdConnect` | 0 | `Client` (`msClientParam`) | handshake, see above |
| `CmdSubscribe` | 1 | *(none)* | `ClientID` identifies the session |
| `CmdUnsubscribe` | 2 | *(none)* | |
| `CmdDisconnect` | 3 | *(none)* | |
| `CmdControl` | 4 | `Control` (`msControlParam`) | via `sendControl`, not `sendRequest` |
| `CmdApplyConfig` | 5 | *(none — uses `Properties`)* | generic property-list apply, see §5 |
| `CmdChannelOpen` | 20 | `Channel` (`msChannelParam{Name}`) | reply carries full `msChannel` incl. assigned `HandleID` |
| `CmdChannelClose/Reset/Start/Stop` | 21–24 | *(none — `HandleID` only)* | `Start`/`Stop` almost certainly begin/end actual RF transmission |
| `CmdProgramAdd` | 25 | `Content` (`msMediaContent`) | wires `SourceID`+`ProgramID`+`EngineID`+stream/node graph together |
| `CmdProgramCommit/Reset/Apply` | 26–28 | *(none / `Properties`)* | |
| `CmdSourceOpen` | 40 | `Source` (`msSourceParam`) | `Mode` selects URL/Capture/Transcode/Resample |
| `CmdSourceClose/Start/Stop/Apply/Reset` | 41–45 | *(none / `Properties`)* | |
| `CmdCaptureOpen/Close/Start/Stop` | 50–53 | *(none — likely `Properties`)* | no `Capture` oneof branch exists at all |
| `CmdEngineApply` | 60 | *(none — likely `Properties`)* | no `Engine` oneof branch, no Open/Close for engines either |
| `CmdMax` | 255 | — | sentinel, not a real command |

There is **no `CmdOutputOpen`/`Close`/`Apply`** — outputs (including the
RF modulator) appear to be fixed/pre-enumerated hardware, discovered via
`msClient.Outputs` at connect time rather than created per-session. See
§5 for how their parameters are actually changed.

### `msEventID` → `msEvent.Param` pairing

| EventID prefix | paired `msEvent.Param` branch |
|---|---|
| `EventService*` | `Status` / *(none)* |
| `EventStorage*` | `Path` (`msStoragePath`) |
| `EventControlProgress` | `Progress` (`int32`) |
| `EventControlFirmware` | `Firmware` (`msFirmwareFile`) |
| `EventControlParam` | `Control` (`msControlParam`) |
| `EventControlResult` | `Result` (`msEventResult`) |
| `EventOutput*` | `Output` (`msOutput`) |
| `EventCapture*` | `Capture` (`msCapture`) |
| `EventChannel*` | `Channel` (`msChannel`) / `Update` for property-change notifications |
| `EventSource*` | `Source` (`msSource`) |
| `EventDebug` | `Profiler` (`bytes`) |

## 5. The generic property system

This is the single most important mechanism in the protocol: **there is
no dedicated protobuf message for ISDB-T modulation parameters (or most
other per-object settings)**. Instead, every stateful object
(`msOutput`, `msChannel`, `msSource`, `msCapture`, `msEngine`) carries a
`repeated msProperty Properties` field, and `msProperty` is a
self-describing (shape, value) pair:

```proto
message msProperty {
  msDescriptor Property = 1;   // the SHAPE: what fields exist
  msPropertyParam Param = 2;   // the VALUES: current data
}
```

- **`msDescriptor`** (`ms_property.proto`) is effectively a runtime
  reflection of a native C struct: `Name`, `Size` (bytes — a strong signal
  this mirrors real memory layout inside `mnservice.exe`, not just
  documentation), and an ordered list of `msPropertyField`.
- **`msPropertyField`** describes one struct member: `Name`, `msFieldType`
  (`FieldNumber`/`FieldSelect`/`FieldFlags`/`FieldString`/`FieldBuffer`/
  `FieldGroup`/`FieldConstSelect`/`FieldList`), its `Offset`/`Size`/`Tag`
  within the struct, whether it `IsSubGroup` (nested struct), an optional
  `msPropertyRange` (legal-value constraint), and a `FieldID` — the
  number used to address *this field's value* independently of its shape.
  A `FieldGroup`-typed field's `Range.RangeGroup.StructDesc` points at a
  **child `msDescriptor`**, so descriptors nest arbitrarily deep — this is
  how something like `mModulationParam.Spec.ARIB_STD_B10.RegionID`-style
  dotted paths (referenced in the GUI's own config code) are represented
  on the wire: as nested descriptor groups, not as literal dotted strings.
- **`msPropertyRange`** is the published (not enforced — see §6) legal
  range for one field: a oneof of `RangeInt{Min,Max,Default}`,
  `RangeUint{Max,IsHex,Default}`, `RangeValues{Values:[msRangeValue{Value,
  Name}, ...]}` (an enumerated/select list — this is how something like
  `Constellation` exposes its `QAM_64`/`QAM_16`/... choices **without any
  wire-level enum type**), `RangeString{Length,Default}`,
  `RangeBuffer{Size,Default}`, or `RangeGroup{StructDesc}` for nested
  structs.
- **`msVariant`** is one concrete value: `Type` (`msVariantType`) +
  `FieldID` (which field this answers) + a oneof of `IntVal`/`UintVal`/
  `StrVal`/`RawVal`.
- **`msPropertyParam`** (`Name` + `repeated msVariant Values`) is what
  actually travels in `msRequest.Properties` / `msEventUpdate.Properties`
  to **get or set** values — it's just a name plus a flat list of
  `(FieldID, value)` pairs.

### How a client would use it

1. **Discover.** Call `connectService`. Walk `msResponse.Client.Outputs`
   (or `Channels`/`Sources`/...). For each object, its `Properties` list
   already contains the full shape (`msDescriptor`) *and* current values
   (`msPropertyParam`) — no separate "describe" RPC is needed; discovery
   is a side effect of listing objects. Recurse into `FieldGroup` fields'
   nested `msDescriptor` for sub-structures.
2. **Get.** Match a `msPropertyField.FieldID` (found by name in the
   descriptor) against the `msVariant.FieldID` entries in the paired
   `msPropertyParam.Values` to read the field's current value and its
   `msVariantType`-tagged oneof payload.
3. **Set.** Build a new `msPropertyParam` with the same `Name` as the
   target descriptor and a `msVariant` per field you want to change
   (`FieldID` + the appropriately-typed oneof value), and send it back —
   most plausibly via `CmdApplyConfig` (`sendRequest`) with
   `msRequest.Properties = [that msPropertyParam]`, since that's the only
   command whose name matches "apply property values" and no
   `CmdOutputApply` exists.

### Worked example: ISDB-T modulation

Using the real-world example values already recovered from the GUI's own
local config JSON (`Channel: UHF_13, PowerLevel: 80, CodeRate: CR_5_6,
GuardInterval: GI_1_16, FFT: FFT_8K, TimeInterleave: TI_MODE_3,
Constellation: QAM_64`), and the debug-mode findings already on record
(`docs/architecture.md` §3: these same 5 fields — Constellation, CodeRate,
GuardInterval, FFT, TimeInterleave — are the ones the GUI hides unless
`EnableDebugMode` is set, i.e. they exist on the wire regardless of GUI
mode), a plausible reconstructed request to set `Constellation = QAM_64`
on the RF-modulation output looks like:

```jsonc
// 1. connectService -> locate the output object
// msResponse.Client.Outputs[i] where ObjectType == ObjectOutputModulation
// -> HandleID = 0x00000042 (example), Properties[0].Property.Name == "mModulationParam"
// -> walk Properties[0].Property.Fields[] to find { Name: "Constellation", FieldID: 7, Type: FieldSelect,
//      Range: { RangeType: RangeSelect, RangeValues: { Values: [
//          { Value: 0, Name: "QAM_64" }, { Value: 1, Name: "QAM_16" }, ... ] } } }
// (FieldID and enumerated Values here are illustrative placeholders for the
//  shape — actual numbers were not captured from a live session.)

// 2. sendRequest — apply the new value
msRequest {
  Cmd: CmdApplyConfig
  ClientID: <handle from step 1's msClient.HandleID>
  HandleID: 0x00000042                 // the output object's HandleID
  Properties: [
    {
      Name: "mModulationParam"
      Values: [
        { Type: VariantUint, FieldID: 7, UintVal: 0 }   // Constellation = QAM_64
        // additional msVariant entries for CodeRate / GuardInterval / FFT /
        // TimeInterleave / PowerLevel / Channel would ride in the same list
      ]
    }
  ]
}
```

The key point is that **the client never needs a compiled-in enum or
struct for "modulation parameters"** — it discovers the field names,
IDs, types, and legal value sets entirely at runtime from the
`msDescriptor`/`msPropertyRange` returned at connect time, then speaks
back using only the generic `msVariant`/`msPropertyParam` vocabulary
defined in `ms_property.proto`. This also means **any field the device
firmware exposes through this mechanism is reachable by a from-scratch
client, including ones the official GUI's Simple/Advanced modes never
surface** (matching the existing `EnableDebugMode` finding in
`docs/architecture.md`).

`msConfigFile` (also in `ms_property.proto`) is a related, separate tree
built from the same primitives (`msVariant`, `msPropertyRange`,
`msProperty`) used for the persisted, session-wide configuration document
rather than a single object's live properties.

## 6. Surprising / notable findings

- **RF parameters have no wire-level enforcement.** `msPropertyRange`
  (Min/Max/enumerated Values) is metadata the *server publishes*; nothing
  in the protocol itself stops a client from sending a `msVariant` outside
  the declared range. XHEAD-USB is a UHF-band RF transmitter, and the
  same property mechanism that carries `PowerLevel`/`Constellation`/etc.
  is fully generic — whether `mnservice.exe` actually validates values
  server-side before writing them (they very likely map directly onto a
  native struct given `msPropertyField.Offset`/`Size`/`Tag`) is **not
  verifiable from this client-side schema alone** and would need native
  binary analysis. Flagging this as the most legally/safety-relevant item
  found.
- **Firmware flashing is exposed on the same local gRPC surface** as
  everything else (`sendControl` + `msControlParam` +
  `msFirmwareFile`/`msFWUsbConfig`, reported via
  `EventControlFirmware`/`EventControlProgress`/`EventControlResult`).
  Access control is only the self-asserted `msPrivilege`
  (`PrivilegeNull`/`PrivilegeDebug`/`PrivilegeControl`) claimed in
  `msClientParam.Privilege` at `CmdConnect` — no cryptographic or
  external authentication is visible in this schema (`localhost`-only
  binding is presumably the intended security boundary).
- **No `CmdOutputOpen/Close/Apply` exists at all**, even though `msOutput`
  is a first-class object with its own `Properties`. Outputs are
  discovered read-only via `msClient.Outputs` at connect time; the RF
  modulator is apparently treated as fixed hardware rather than a
  session-scoped resource.
- **`msObjectType.ObjectChannelXHEAD` sits alongside
  `ObjectChannelTransform`/`Codec`/`File`/`Program`** — strong evidence
  `mnservice.exe`'s channel pipeline is a generic, multi-backend broadcast
  framework shared across other (non-XHEAD) Micomsoft products, with
  XHEAD-USB being just one selectable hardware output backend rather than
  a bespoke single-purpose service.
- **Built-in self-test signal generators**: `msColorbarMode` (test
  patterns: SMPTE bars, PAL bars, black, etc.) and `msSineToneMode`
  (mute/beep/no-beep tone) are wired directly into the transcode/resample
  source params — a from-scratch client can validate the whole RF chain
  end-to-end without needing real source media.
- **`msPropertyField.Offset`/`Size`/`Tag`/`OffsetGroup`** strongly suggest
  the property/descriptor system is a live reflection of native C struct
  layouts inside `mnservice.exe`, not an abstraction layer — reinforcing
  the point above about unenforced ranges potentially having direct
  hardware effects.
- Field numbering in this schema consistently reserves blocks of 10 (10s,
  20s, 30s) or larger round numbers (50s, 60s, 70s, 80s) for oneof
  alternatives while plain fields use 1–9 — a deliberate schema-evolution
  convention (room to add plain fields later without renumbering oneofs),
  observed uniformly across all 9 files.

## 7. Supporting (non-wire) types

None. Every one of the 88 `.cs` files in `mnFramework.grpc/` is
protobuf/grpc-generated code: the 60 message-class files carry
`[GeneratedCode]`/`IMessage<T>` markers, the service file
(`msBroadcastService.cs`) carries `[GeneratedCode("grpc_csharp_plugin",
...)]` on every RPC stub, and the remaining 28 enum files — which don't
carry those same attributes since C# enums can't implement `IMessage<T>`
— instead carry `[OriginalName(...)]` attributes from
`Google.Protobuf.Reflection`, an equally reliable protoc-csharp
fingerprint. There is no separate "hand-written wrapper" tier of types in
this folder (that distinction applies to `mnFramework`, the higher-level,
non-protobuf wrapper library, which is out of scope for this task).
