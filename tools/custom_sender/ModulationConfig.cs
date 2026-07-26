namespace XHeadSender
{
    /// <summary>
    /// GUIから自由に設定する変調パラメータ + RF電力設定。FieldID・許容値は
    /// docs/protocol/modulation_capabilities.md の実機検証済み一覧に基づく。既定値は
    /// tools/custom_sender の RunFullPipelineTest / tools/direct_usb --configure で
    /// 動作実績のある安全な値(QPSK・473000kHz・DACGain=-10)と同一。
    /// </summary>
    internal sealed class ModulationConfig
    {
        public uint Frequency = 473000;      // FieldID=0, kHz, 宣言上0-1,000,000
        public int Constellation = 1;        // FieldID=19, 0=DQPSK 1=QPSK 2=QAM16 3=QAM64
        public uint Bandwidth = 6;           // FieldID=20, MHz, 宣言上0-10
        public int FFT = 1;                  // FieldID=21, 0=_2k 1=_8k 2=_4k
        public int CodeRate = 3;             // FieldID=22, 0=1/2 1=2/3 2=3/4 3=5/6 4=7/8
        public int GuardInterval = 1;        // FieldID=23, 0=1/32 1=1/16 2=1/8 3=1/4
        public int TimeInterleavce = 3;      // FieldID=24, 1=Mode1 2=Mode2 3=Mode3
        public uint Level = 90;              // mPSRFPowerAdjust FieldID=0, 80-100
        public int PAGain = 2;               // mPSRFPowerAdjust FieldID=1, int8
        public int DACGain = -10;            // mPSRFPowerAdjust FieldID=2, int8

        // チャンネル/番組メタデータ (Spec=ARIB_STD_B10 前提、mMTSChannelParam/mMTSProgramParam)。
        // 実受信機でチャンネル一覧に表示される名前等。2026-07-26に一度「どのフィールドを
        // 触ってもChannelStartがハングする」という重大な問題を検出したが、実機USB接続の劣化
        // (長時間の強制終了・生レジスタ操作の繰り返しが原因)によるものと判明し、物理的な
        // 抜き差しで解消・再検証済み(docs/protocol/modulation_capabilities.md「続報14」)。
        public uint RegionID = 23;           // mMTSChannelParam FieldID=4, 0-63, 都道府県域識別
        public uint BroadcasterID = 1;       // mMTSChannelParam FieldID=5, 0-15
        public uint RemoteControlKeyID = 1;  // mMTSChannelParam FieldID=6, 0-12, リモコンの番号キー
        public string NetworkName = "VAT-01";    // mMTSChannelParam FieldID=7, maxlen=16
        public string TSName = "VAT-01";         // mMTSChannelParam FieldID=8, maxlen=16
        public uint ServiceNo = 1;           // mMTSProgramParam FieldID=8, 0-8
        public string ServiceName = "VAT-01";    // mMTSProgramParam FieldID=12, maxlen=16, 受信機のチャンネル名表示
        public int CopyFlag = 0;             // mMTSProgramParam FieldID=11, 0=Free 2=CopyOnce 3=Forbidden

        // EPG (mEPGSimpleParam) -- STUDIOの「EPG設定」タブ相当。1件のみ・繰り返し配信という
        // 制約はハードウェア/ファームウェア側の仕様(docs/protocol/modulation_capabilities.md
        // 「続報11」で確認済み)であり、本ツールでも同じ制約を引き継ぐ。
        public int EPGMode = 257;            // mEPGSimpleParam FieldID=0, 0=Disable 1=PresentFollowingOnly 256=AribPresentFollowingOnly 257=AribSchedule_8Days
        public uint EPGIntervalHours = 1;    // FieldID=1, 0-8
        public uint EPGEventID = 4096;       // FieldID=2, 0-65535
        public int EPGType = 0;              // FieldID=3, 0=Undefine 1=News 2=Sport 3=Movie 4=Drama 5=Music 6=Tabloidshow 7=Varietyshow 8=Animation 9=Documentary 10=Performance 11=Education 12=Welfare 255=Others
        public string EPGTitle = "VA-TV";        // FieldID=4, maxlen=256
        public string EPGDescriptor = "VA-TV";   // FieldID=5, maxlen=256

        // メディア/コーデック設定 (mPSEncodeParam) -- STUDIOの「メディア設定」(Video/Audio PID)
        // ・「コーデック設定」タブに相当。実受信機での見え方・エンコード品質を調整する。
        public int EncodePerformance = 2;    // FieldID=0, 2=Fast 3=Standard 4=Slow 5=Slower
        public uint VideoPID = 0x0110;       // FieldID=2, 0-8191 (hex表示)
        public uint AudioPID = 0x0120;       // FieldID=3, 0-8191 (hex表示)
        public uint Latency = 500;           // FieldID=4, 0-1000 (ms)
        public uint QueueTime = 1;           // FieldID=5, 0-30 (秒)
        public int VideoResolution = 1;      // Video group FieldID=7, 0=_1080P 1=_1080I 2=_1440P 3=_1440I 4=_720P 5=_480P 6=_480I
        public int VideoAspectRatio = 7;     // FieldID=8, 5=SAR_1_1 6=DAR_4_3 7=DAR_16_9 8=DAR_2_21
        public int VideoFrameRate = 3;       // FieldID=11, 0=23.97 1=24 2=25 3=29.97 4=30 5=50 6=59.94 7=60
        public int AudioChannel = 0;         // Audio group FieldID=18, 0=Stereo 2=DualChannel 3=Mono
        public int AudioSampleRate = 48000;  // FieldID=19, 32000-48000
        public int AudioBitrate = 128000;    // FieldID=20, 128000-384000
        public int QualityMode = 0;          // Quality group FieldID=23, 0=CBR 1=VBRAvgBitRate 2=VBRQuality
        public uint GOPLength = 18;          // FieldID=33, 0-60
        public string DebugFile = "";        // FieldID=37, maxlen無制限。空文字ならエンコーダのデバッグダンプ出力を無効化
        public string BMLFile = "";          // FieldID=38, maxlen無制限。.xbmlファイルへのローカルパス(データ放送/字幕再注入)
    }
}
