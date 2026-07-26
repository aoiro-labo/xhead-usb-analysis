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
    }
}
