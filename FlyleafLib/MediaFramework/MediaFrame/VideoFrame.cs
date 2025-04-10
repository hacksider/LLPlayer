﻿using Vortice.Direct3D11;

using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace FlyleafLib.MediaFramework.MediaFrame;

public unsafe class VideoFrame : FrameBase
{
    public ID3D11Texture2D[]            textures;       // Planes
    public ID3D11ShaderResourceView[]   srvs;           // Views

    // Zero-Copy
    public AVFrame*                     avFrame;        // Lets ffmpeg to know that we still need it
}
