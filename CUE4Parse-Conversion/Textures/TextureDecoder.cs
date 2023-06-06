using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse_Conversion.Textures.ASTC;
using CUE4Parse_Conversion.Textures.BC;
using CUE4Parse_Conversion.Textures.DXT;
using CUE4Parse.UE4.Exceptions;
using SkiaSharp;
using static CUE4Parse.Utils.TypeConversionUtils;
using System.Collections.Generic;

namespace CUE4Parse_Conversion.Textures;

public static class TextureDecoder
{
    public static SKBitmap? Decode(this UTexture texture, ETexturePlatform platform = ETexturePlatform.DesktopMobile) => texture.IsVirtual && platform == ETexturePlatform.DesktopMobile ? texture.Decode(texture.PlatformData.VTData, platform) : texture.Decode(texture.GetFirstMip(), platform);

    public static SKBitmap? Decode(this UTexture texture, FVirtualTextureBuiltData? vtdata, ETexturePlatform platform = ETexturePlatform.DesktopMobile)
    {
        Int32 part1by1(Int32 n)
        {
            n &= 0x0000ffff;
            n = (n | (n << 8)) & 0x00FF00FF;
            n = (n | (n << 4)) & 0x0F0F0F0F;
            n = (n | (n << 2)) & 0x33333333;
            n = (n | (n << 1)) & 0x55555555;

            return n;
        }

        int UpperBound<T>(T[] array, T value)
        {
            if (array.Length == 0)
                return -1;

            Comparer<T> comparer = Comparer<T>.Default;
            int start = 0;
            int end = array.Length - 1;
            int mid;

            do
            {
                mid = (start + end) / 2;

                if (comparer.Compare(array[mid], value) <= 0)
                {
                    if (mid < (array.Length - 1) && comparer.Compare(value, array[mid + 1]) < 0)
                        return mid + 1;

                    start = mid + 1;
                }
                else
                    end = mid - 1;
            } while (start <= end);

            if (comparer.Compare(value, array[0]) < 0)
                return 0;

            return mid + 1;
        }

        if (vtdata == null)
            return null;

        FVirtualTextureDataChunk chunk = vtdata.Chunks[0];
        byte[] bulk_data = chunk.BulkData.Data;
        uint chunk_offset = vtdata.BaseOffsetPerMip[0];

        int width = (int) vtdata.Width;
        int height = (int) vtdata.Height;

        uint[] Addresses = vtdata.TileOffsetData[0].Addresses;
        uint[] Offsets = vtdata.TileOffsetData[0].Offsets;

        int tile_size_px = (int) vtdata.TileSize;
        uint tile_size_bytes = vtdata.TileDataOffsetPerLayer.Last();
        int border_size = (int) vtdata.TileBorderSize;
        int tile_width = tile_size_px + border_size * 2;
        int tile_height = tile_size_px + border_size * 2;
        int tile_z = (int) vtdata.NumLayers;

        int rows = height / tile_size_px;
        int cols = width / tile_size_px;

        EPixelFormat format = texture.Format;
        if (PixelFormatUtils.PixelFormats.ElementAtOrDefault((int) format) is not { Supported: true } formatInfo || formatInfo.BlockBytes == 0)
            throw new NotImplementedException($"The supplied pixel format {format} is not supported!");

        using SKSurface surface = SKSurface.Create(new SKImageInfo(width, height));
        using SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                uint tile_id = (uint) (part1by1(col) | (part1by1(row) << 1));
                uint tile_offset;
                int BlockIndex = UpperBound(Addresses, tile_id) - 1;
                uint BaseOffset = Offsets[BlockIndex];

                if (BaseOffset == ~0u)
                    continue;
                else
                {
                    uint BaseAddress = Addresses[BlockIndex];
                    uint LocalOffset = tile_id - BaseAddress;
                    tile_offset = chunk_offset + ((BaseOffset + LocalOffset) * tile_size_bytes);
                }

                if (tile_offset >= chunk.SizeInBytes)
                    continue;

                byte[] data = bulk_data.Skip((int) tile_offset).Take((int) tile_size_bytes).ToArray();
                SKColorType colorType;

                switch (format)
                {
                    case EPixelFormat.PF_DXT1:
                        data = DXTDecoder.DXT1(data, tile_width, tile_height, tile_z);
                        colorType = SKColorType.Rgba8888;
                        break;
                    case EPixelFormat.PF_DXT5:
                        data = DXTDecoder.DXT5(data, tile_width, tile_height, tile_z);
                        colorType = SKColorType.Rgba8888;
                        break;
                    case EPixelFormat.PF_ASTC_4x4:
                    case EPixelFormat.PF_ASTC_6x6:
                    case EPixelFormat.PF_ASTC_8x8:
                    case EPixelFormat.PF_ASTC_10x10:
                    case EPixelFormat.PF_ASTC_12x12:
                        data = ASTCDecoder.RGBA8888(
                            data,
                            formatInfo.BlockSizeX,
                            formatInfo.BlockSizeY,
                            formatInfo.BlockSizeZ,
                            tile_width, tile_height, tile_z);
                        colorType = SKColorType.Rgba8888;

                        if (texture.IsNormalMap)
                        {
                            unsafe
                            {
                                var offset = 0;
                                fixed (byte* d = data)
                                {
                                    for (var i = 0; i < tile_width * tile_height; i++)
                                    {
                                        d[offset + 2] = BCDecoder.GetZNormal(d[offset], d[offset + 1]);
                                        offset += 4;
                                    }
                                }
                            }
                        }

                        break;
                    case EPixelFormat.PF_BC4:
                        data = BCDecoder.BC4(data, tile_width, tile_height);
                        colorType = SKColorType.Rgb888x;
                        break;
                    case EPixelFormat.PF_BC5:
                        data = BCDecoder.BC5(data, tile_width, tile_height);
                        colorType = SKColorType.Rgb888x;
                        break;
                    case EPixelFormat.PF_BC6H:
                        data = Detex.DecodeDetexLinear(data, tile_width, tile_height, true,
                            DetexTextureFormat.DETEX_TEXTURE_FORMAT_BPTC_FLOAT,
                            DetexPixelFormat.DETEX_PIXEL_FORMAT_FLOAT_RGBX16);
                        colorType = SKColorType.Rgb565;
                        break;
                    case EPixelFormat.PF_BC7:
                        data = Detex.DecodeDetexLinear(data, tile_width, tile_height, false,
                            DetexTextureFormat.DETEX_TEXTURE_FORMAT_BPTC,
                            DetexPixelFormat.DETEX_PIXEL_FORMAT_RGBA8);
                        colorType = SKColorType.Rgba8888;
                        break;
                    case EPixelFormat.PF_ETC1:
                        data = Detex.DecodeDetexLinear(data, tile_width, tile_height, false,
                            DetexTextureFormat.DETEX_TEXTURE_FORMAT_ETC1,
                            DetexPixelFormat.DETEX_PIXEL_FORMAT_RGBA8);
                        colorType = SKColorType.Rgba8888;
                        break;
                    case EPixelFormat.PF_ETC2_RGB:
                        data = Detex.DecodeDetexLinear(data, tile_width, tile_height, false,
                            DetexTextureFormat.DETEX_TEXTURE_FORMAT_ETC2,
                            DetexPixelFormat.DETEX_PIXEL_FORMAT_RGBA8);
                        colorType = SKColorType.Rgba8888;
                        break;
                    case EPixelFormat.PF_ETC2_RGBA:
                        data = Detex.DecodeDetexLinear(data, tile_width, tile_height, false,
                            DetexTextureFormat.DETEX_TEXTURE_FORMAT_ETC2_EAC,
                            DetexPixelFormat.DETEX_PIXEL_FORMAT_RGBA8);
                        colorType = SKColorType.Rgba8888;
                        break;
                    case EPixelFormat.PF_R16F:
                    case EPixelFormat.PF_R16F_FILTER:
                    case EPixelFormat.PF_G16:
                        unsafe
                        {
                            fixed (byte* d = data)
                            {
                                data = ConvertRawR16DataToRGB888X(tile_width, tile_height, d, tile_width * 2);
                            }
                        }

                        colorType = SKColorType.Rgb888x;
                        break;
                    case EPixelFormat.PF_B8G8R8A8:
                        colorType = SKColorType.Bgra8888;
                        break;
                    case EPixelFormat.PF_G8:
                        colorType = SKColorType.Gray8;
                        break;
                    case EPixelFormat.PF_FloatRGBA:
                        unsafe
                        {
                            fixed (byte* d = data)
                            {
                                data = ConvertRawR16G16B16A16FDataToRGBA8888(tile_width, tile_height, d, tile_width * 8, false);
                            }
                        }

                        colorType = SKColorType.Rgba8888;
                        break;
                    default:
                        throw new NotImplementedException($"Unknown pixel format: {format}");
                }

                var info = new SKImageInfo(tile_width, tile_height, colorType, SKAlphaType.Unpremul);
                using var bitmap = new SKBitmap();

                unsafe
                {
                    var pixelsPtr = NativeMemory.Alloc((nuint) data.Length);
                    fixed (byte* p = data)
                    {
                        Unsafe.CopyBlockUnaligned(pixelsPtr, p, (uint) data.Length);
                    }

                    bitmap.InstallPixels(info, new IntPtr(pixelsPtr), info.RowBytes, (address, _) => NativeMemory.Free(address.ToPointer()));
                }

                canvas.DrawBitmap(bitmap, (tile_size_px * col) - (col == 0 ? border_size : 0), (tile_size_px * row) - (row == 0 ? border_size : 0));
            }
        }

        return SKBitmap.FromImage(surface.Snapshot());
    }

    public static SKBitmap? Decode(this UTexture texture, FTexture2DMipMap? mip, ETexturePlatform platform = ETexturePlatform.DesktopMobile)
    {
        if (!texture.IsVirtual && mip != null)
        {
            DecodeTexture(mip, texture.Format, texture.IsNormalMap, platform, out var data, out var colorType);

            var width = mip.SizeX;
            var height = mip.SizeY;
            var info = new SKImageInfo(width, height, colorType, SKAlphaType.Unpremul);
            var bitmap = new SKBitmap();

            unsafe
            {
                var pixelsPtr = NativeMemory.Alloc((nuint) data.Length);
                fixed (byte* p = data)
                {
                    Unsafe.CopyBlockUnaligned(pixelsPtr, p, (uint) data.Length);
                }

                bitmap.InstallPixels(info, new IntPtr(pixelsPtr), info.RowBytes, (address, _) => NativeMemory.Free(address.ToPointer()));
            }

            if (!texture.RenderNearestNeighbor)
            {
                return bitmap;
            }

            var resized = bitmap.Resize(new SKImageInfo(width, height), SKFilterQuality.None);
            bitmap.Dispose();
            return resized;
        }

        return null;
    }

    public static void DecodeTexture(FTexture2DMipMap? mip, EPixelFormat format, bool isNormalMap, ETexturePlatform platform, out byte[] data, out SKColorType colorType)
    {
        if (mip?.BulkData.Data is not { Length: > 0 }) throw new ParserException("Supplied MipMap is null or has empty data!");
        if (PixelFormatUtils.PixelFormats.ElementAtOrDefault((int) format) is not { Supported: true } formatInfo || formatInfo.BlockBytes == 0) throw new NotImplementedException($"The supplied pixel format {format} is not supported!");

        var isPS = platform == ETexturePlatform.Playstation;
        var isNX = platform == ETexturePlatform.NintendoSwitch;

        // If the platform requires deswizzling, check if we should even try.
        if (isPS || isNX)
        {
            var blockSizeX = mip.SizeX / formatInfo.BlockSizeX;
            var blockSizeY = mip.SizeY / formatInfo.BlockSizeY;
            var totalBlocks = mip.BulkData.Data.Length / formatInfo.BlockBytes;
            if (blockSizeX * blockSizeY > totalBlocks) throw new ParserException("The supplied MipMap could not be untiled!");
        }

        var bytes = mip.BulkData.Data;

        // Handle deswizzling if necessary.
        if (isPS) bytes = PlatformDeswizzlers.DeswizzlePS4(bytes, mip, formatInfo);
        else if (isNX) bytes = PlatformDeswizzlers.GetDeswizzledData(bytes, mip, formatInfo);

        switch (format)
        {
            case EPixelFormat.PF_DXT1:
            {
                data = DXTDecoder.DXT1(bytes, mip.SizeX, mip.SizeY, mip.SizeZ);
                colorType = SKColorType.Rgba8888;
                break;
            }
            case EPixelFormat.PF_DXT5:
                data = DXTDecoder.DXT5(bytes, mip.SizeX, mip.SizeY, mip.SizeZ);
                colorType = SKColorType.Rgba8888;
                break;
            case EPixelFormat.PF_ASTC_4x4:
            case EPixelFormat.PF_ASTC_6x6:
            case EPixelFormat.PF_ASTC_8x8:
            case EPixelFormat.PF_ASTC_10x10:
            case EPixelFormat.PF_ASTC_12x12:
                data = ASTCDecoder.RGBA8888(
                    bytes,
                    formatInfo.BlockSizeX,
                    formatInfo.BlockSizeY,
                    formatInfo.BlockSizeZ,
                    mip.SizeX, mip.SizeY, mip.SizeZ);
                colorType = SKColorType.Rgba8888;

                if (isNormalMap)
                {
                    // UE4 drops blue channel for normal maps before encoding, restore it
                    unsafe
                    {
                        var offset = 0;
                        fixed (byte* d = data)
                        {
                            for (var i = 0; i < mip.SizeX * mip.SizeY; i++)
                            {
                                d[offset + 2] = BCDecoder.GetZNormal(d[offset], d[offset + 1]);
                                offset += 4;
                            }
                        }
                    }
                }

                break;
            case EPixelFormat.PF_BC4:
                data = BCDecoder.BC4(bytes, mip.SizeX, mip.SizeY);
                colorType = SKColorType.Rgb888x;
                break;
            case EPixelFormat.PF_BC5:
                data = BCDecoder.BC5(bytes, mip.SizeX, mip.SizeY);
                colorType = SKColorType.Rgb888x;
                break;
            case EPixelFormat.PF_BC6H:
                // BC6H doesn't work no matter the pixel format, the closest we can get is either
                // Rgb565 DETEX_PIXEL_FORMAT_FLOAT_RGBX16 or Rgb565 DETEX_PIXEL_FORMAT_FLOAT_BGRX16

                data = Detex.DecodeDetexLinear(bytes, mip.SizeX, mip.SizeY, true,
                    DetexTextureFormat.DETEX_TEXTURE_FORMAT_BPTC_FLOAT,
                    DetexPixelFormat.DETEX_PIXEL_FORMAT_FLOAT_RGBX16);
                colorType = SKColorType.Rgb565;
                break;
            case EPixelFormat.PF_BC7:
                data = Detex.DecodeDetexLinear(bytes, mip.SizeX, mip.SizeY, false,
                    DetexTextureFormat.DETEX_TEXTURE_FORMAT_BPTC,
                    DetexPixelFormat.DETEX_PIXEL_FORMAT_RGBA8);
                colorType = SKColorType.Rgba8888;
                break;
            case EPixelFormat.PF_ETC1:
                data = Detex.DecodeDetexLinear(bytes, mip.SizeX, mip.SizeY, false,
                    DetexTextureFormat.DETEX_TEXTURE_FORMAT_ETC1,
                    DetexPixelFormat.DETEX_PIXEL_FORMAT_RGBA8);
                colorType = SKColorType.Rgba8888;
                break;
            case EPixelFormat.PF_ETC2_RGB:
                data = Detex.DecodeDetexLinear(bytes, mip.SizeX, mip.SizeY, false,
                    DetexTextureFormat.DETEX_TEXTURE_FORMAT_ETC2,
                    DetexPixelFormat.DETEX_PIXEL_FORMAT_RGBA8);
                colorType = SKColorType.Rgba8888;
                break;
            case EPixelFormat.PF_ETC2_RGBA:
                data = Detex.DecodeDetexLinear(bytes, mip.SizeX, mip.SizeY, false,
                    DetexTextureFormat.DETEX_TEXTURE_FORMAT_ETC2_EAC,
                    DetexPixelFormat.DETEX_PIXEL_FORMAT_RGBA8);
                colorType = SKColorType.Rgba8888;
                break;
            case EPixelFormat.PF_R16F:
            case EPixelFormat.PF_R16F_FILTER:
            case EPixelFormat.PF_G16:
                unsafe
                {
                    fixed (byte* d = bytes)
                    {
                        data = ConvertRawR16DataToRGB888X(mip.SizeX, mip.SizeY, d, mip.SizeX * 2); // 2 BPP
                    }
                }

                colorType = SKColorType.Rgb888x;
                break;
            case EPixelFormat.PF_B8G8R8A8:
                data = bytes;
                colorType = SKColorType.Bgra8888;
                break;
            case EPixelFormat.PF_G8:
                data = bytes;
                colorType = SKColorType.Gray8;
                break;
            case EPixelFormat.PF_FloatRGBA:
                unsafe
                {
                    fixed (byte* d = bytes)
                    {
                        data = ConvertRawR16G16B16A16FDataToRGBA8888(mip.SizeX, mip.SizeY, d, mip.SizeX * 8, false); // 8 BPP
                    }
                }

                colorType = SKColorType.Rgba8888;
                break;
            default: throw new NotImplementedException($"Unknown pixel format: {format}");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte[] ConvertRawR16DataToRGB888X(int width, int height, byte* inp, int srcPitch)
    {
        // e.g. shadow maps
        var ret = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            var srcPtr = (ushort*) (inp + y * srcPitch);
            var destPtr = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                var value16 = *srcPtr++;
                var value = FColor.Requantize16to8(value16);

                ret[destPtr++] = value;
                ret[destPtr++] = value;
                ret[destPtr++] = value;
                ret[destPtr++] = 255;
            }
        }

        return ret;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte[] ConvertRawR16G16B16A16FDataToRGBA8888(int width, int height, byte* inp, int srcPitch, bool linearToGamma)
    {
        float minR = 0.0f, minG = 0.0f, minB = 0.0f, minA = 0.0f;
        float maxR = 1.0f, maxG = 1.0f, maxB = 1.0f, maxA = 1.0f;

        for (int y = 0; y < height; y++)
        {
            var srcPtr = (ushort*) (inp + y * srcPitch);

            for (int x = 0; x < width; x++)
            {
                minR = MathF.Min(HalfToFloat(srcPtr[0]), minR);
                minG = MathF.Min(HalfToFloat(srcPtr[1]), minG);
                minB = MathF.Min(HalfToFloat(srcPtr[2]), minB);
                minA = MathF.Min(HalfToFloat(srcPtr[3]), minA);
                maxR = MathF.Max(HalfToFloat(srcPtr[0]), maxR);
                maxG = MathF.Max(HalfToFloat(srcPtr[1]), maxG);
                maxB = MathF.Max(HalfToFloat(srcPtr[2]), maxB);
                maxA = MathF.Max(HalfToFloat(srcPtr[3]), maxA);
                srcPtr += 4;
            }
        }

        var ret = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            var srcPtr = (ushort*) (inp + y * srcPitch);
            var destPtr = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                var color = new FLinearColor(
                    (HalfToFloat(*srcPtr++) - minR) / (maxR - minR),
                    (HalfToFloat(*srcPtr++) - minG) / (maxG - minG),
                    (HalfToFloat(*srcPtr++) - minB) / (maxB - minB),
                    (HalfToFloat(*srcPtr++) - minA) / (maxA - minA)
                ).ToFColor(linearToGamma);
                ret[destPtr++] = color.R;
                ret[destPtr++] = color.G;
                ret[destPtr++] = color.B;
                ret[destPtr++] = color.A;
            }
        }

        return ret;
    }
}
