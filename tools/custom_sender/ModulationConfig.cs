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
    }
}
