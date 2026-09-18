#region HEADER
/* This file was derived from libmspack
 * (C) 2003-2004 Stuart Caie.
 * (C) 2011 Ali Scissons.
 *
 * The LZX method was created by Jonathan Forbes and Tomi Poutanen, adapted
 * by Microsoft Corporation.
 *
 * This source file is Dual licensed; meaning the end-user of this source file
 * may redistribute/modify it under the LGPL 2.1 or MS-PL licenses.
 */
#region LGPL License
/* GNU LESSER GENERAL PUBLIC LICENSE version 2.1
 * LzxDecoder is free software; you can redistribute it and/or modify it under
 * the terms of the GNU Lesser General Public License (LGPL) version 2.1
 */
#endregion
#region MS-PL License
/*
 * MICROSOFT PUBLIC LICENSE
 * This source code is subject to the terms of the Microsoft Public License (Ms-PL).
 *
 * Redistribution and use in source and binary forms, with or without modification,
 * is permitted provided that redistributions of the source code retain the above
 * copyright notices and this file header.
 *
 * Additional copyright notices should be appended to the list above.
 *
 * For details, see <http://www.opensource.org/licenses/ms-pl.html>.
 */
#endregion
/*
 * This derived work is recognized by Stuart Caie and is authorized to adapt
 * any changes made to lzxd.c in his libmspack library and will still retain
 * this dual licensing scheme. Big thanks to Stuart Caie!
 *
 * DETAILS
 * This file is a pure C# port of the lzxd.c file from libmspack, with minor
 * changes towards the decompression of XNB files. The original decompression
 * software of LZX encoded data was written by Suart Caie in his
 * libmspack/cabextract projects, which can be located at
 * http://http://www.cabextract.org.uk/
 *
 * Ported into this project (unmodified algorithm) from the MonoGame project
 * (MonoGame.Framework/Content/LzxDecoder.cs, MIT-licensed project, this file
 * dual LGPL2.1/MS-PL as noted above) purely so this standalone tool has no
 * runtime dependency on the game's own MonoGame.Framework.dll -- that DLL is
 * built for x64 and can't be loaded from this Mac's arm64 .NET SDK.
 */
#endregion

using System;
using System.IO;

namespace XnbStringTool
{
    /// <summary>
    /// Decodes the LZX-compressed body of an XNB file. This is the same
    /// compression XNA/MonoGame content uses; ported here so we don't need
    /// to link against the game's own (architecture-specific) assemblies.
    /// </summary>
    internal class LzxDecoder
    {
        public static uint[]? position_base = null;
        public static byte[]? extra_bits = null;

        private LzxState m_state;

        public LzxDecoder(int window)
        {
            uint wndsize = (uint)(1 << window);
            int posn_slots;

            if (window < 15 || window > 21) throw new UnsupportedWindowSizeRange();

            m_state = new LzxState();
            m_state.actual_size = 0;
            m_state.window = new byte[wndsize];
            for (int i = 0; i < wndsize; i++) m_state.window[i] = 0xDC;
            m_state.actual_size = wndsize;
            m_state.window_size = wndsize;
            m_state.window_posn = 0;

            if (extra_bits == null)
            {
                extra_bits = new byte[52];
                for (int i = 0, j = 0; i <= 50; i += 2)
                {
                    extra_bits[i] = extra_bits[i + 1] = (byte)j;
                    if ((i != 0) && (j < 17)) j++;
                }
            }
            if (position_base == null)
            {
                position_base = new uint[51];
                for (int i = 0, j = 0; i <= 50; i++)
                {
                    position_base[i] = (uint)j;
                    j += 1 << extra_bits[i];
                }
            }

            if (window == 20) posn_slots = 42;
            else if (window == 21) posn_slots = 50;
            else posn_slots = window << 1;

            m_state.R0 = m_state.R1 = m_state.R2 = 1;
            m_state.main_elements = (ushort)(LzxConstants.NUM_CHARS + (posn_slots << 3));
            m_state.header_read = 0;
            m_state.frames_read = 0;
            m_state.block_remaining = 0;
            m_state.block_type = LzxConstants.BLOCKTYPE.INVALID;
            m_state.intel_curpos = 0;
            m_state.intel_started = 0;

            m_state.PRETREE_table = new ushort[(1 << LzxConstants.PRETREE_TABLEBITS) + (LzxConstants.PRETREE_MAXSYMBOLS << 1)];
            m_state.PRETREE_len = new byte[LzxConstants.PRETREE_MAXSYMBOLS + LzxConstants.LENTABLE_SAFETY];
            m_state.MAINTREE_table = new ushort[(1 << LzxConstants.MAINTREE_TABLEBITS) + (LzxConstants.MAINTREE_MAXSYMBOLS << 1)];
            m_state.MAINTREE_len = new byte[LzxConstants.MAINTREE_MAXSYMBOLS + LzxConstants.LENTABLE_SAFETY];
            m_state.LENGTH_table = new ushort[(1 << LzxConstants.LENGTH_TABLEBITS) + (LzxConstants.LENGTH_MAXSYMBOLS << 1)];
            m_state.LENGTH_len = new byte[LzxConstants.LENGTH_MAXSYMBOLS + LzxConstants.LENTABLE_SAFETY];
            m_state.ALIGNED_table = new ushort[(1 << LzxConstants.ALIGNED_TABLEBITS) + (LzxConstants.ALIGNED_MAXSYMBOLS << 1)];
            m_state.ALIGNED_len = new byte[LzxConstants.ALIGNED_MAXSYMBOLS + LzxConstants.LENTABLE_SAFETY];
            for (int i = 0; i < LzxConstants.MAINTREE_MAXSYMBOLS; i++) m_state.MAINTREE_len[i] = 0;
            for (int i = 0; i < LzxConstants.LENGTH_MAXSYMBOLS; i++) m_state.LENGTH_len[i] = 0;
        }

        public int Decompress(Stream inData, int inLen, Stream outData, int outLen)
        {
            BitBuffer bitbuf = new BitBuffer(inData);
            long startpos = inData.Position;
            long endpos = inData.Position + inLen;

            byte[] window = m_state.window;

            uint window_posn = m_state.window_posn;
            uint window_size = m_state.window_size;
            uint R0 = m_state.R0;
            uint R1 = m_state.R1;
            uint R2 = m_state.R2;
            uint i, j;

            int togo = outLen, this_run, main_element, match_length, match_offset, length_footer, extra, verbatim_bits;
            int rundest, runsrc, copy_length, aligned_bits;

            bitbuf.InitBitStream();

            if (m_state.header_read == 0)
            {
                uint intel = bitbuf.ReadBits(1);
                if (intel != 0)
                {
                    i = bitbuf.ReadBits(16); j = bitbuf.ReadBits(16);
                    m_state.intel_filesize = (int)((i << 16) | j);
                }
                m_state.header_read = 1;
            }

            while (togo > 0)
            {
                if (m_state.block_remaining == 0)
                {
                    if (m_state.block_type == LzxConstants.BLOCKTYPE.UNCOMPRESSED)
                    {
                        if ((m_state.block_length & 1) == 1) inData.ReadByte();
                        bitbuf.InitBitStream();
                    }

                    m_state.block_type = (LzxConstants.BLOCKTYPE)bitbuf.ReadBits(3);
                    i = bitbuf.ReadBits(16);
                    j = bitbuf.ReadBits(8);
                    m_state.block_remaining = m_state.block_length = (uint)((i << 8) | j);

                    switch (m_state.block_type)
                    {
                        case LzxConstants.BLOCKTYPE.ALIGNED:
                            for (i = 0, j = 0; i < 8; i++) { j = bitbuf.ReadBits(3); m_state.ALIGNED_len[i] = (byte)j; }
                            MakeDecodeTable(LzxConstants.ALIGNED_MAXSYMBOLS, LzxConstants.ALIGNED_TABLEBITS,
                                            m_state.ALIGNED_len, m_state.ALIGNED_table);
                            goto case LzxConstants.BLOCKTYPE.VERBATIM;

                        case LzxConstants.BLOCKTYPE.VERBATIM:
                            ReadLengths(m_state.MAINTREE_len, 0, 256, bitbuf);
                            ReadLengths(m_state.MAINTREE_len, 256, m_state.main_elements, bitbuf);
                            MakeDecodeTable(LzxConstants.MAINTREE_MAXSYMBOLS, LzxConstants.MAINTREE_TABLEBITS,
                                            m_state.MAINTREE_len, m_state.MAINTREE_table);
                            if (m_state.MAINTREE_len[0xE8] != 0) m_state.intel_started = 1;

                            ReadLengths(m_state.LENGTH_len, 0, LzxConstants.NUM_SECONDARY_LENGTHS, bitbuf);
                            MakeDecodeTable(LzxConstants.LENGTH_MAXSYMBOLS, LzxConstants.LENGTH_TABLEBITS,
                                            m_state.LENGTH_len, m_state.LENGTH_table);
                            break;

                        case LzxConstants.BLOCKTYPE.UNCOMPRESSED:
                            m_state.intel_started = 1;
                            bitbuf.EnsureBits(16);
                            if (bitbuf.GetBitsLeft() > 16) inData.Seek(-2, SeekOrigin.Current);
                            byte hi, mh, ml, lo;
                            lo = (byte)inData.ReadByte(); ml = (byte)inData.ReadByte(); mh = (byte)inData.ReadByte(); hi = (byte)inData.ReadByte();
                            R0 = (uint)(lo | ml << 8 | mh << 16 | hi << 24);
                            lo = (byte)inData.ReadByte(); ml = (byte)inData.ReadByte(); mh = (byte)inData.ReadByte(); hi = (byte)inData.ReadByte();
                            R1 = (uint)(lo | ml << 8 | mh << 16 | hi << 24);
                            lo = (byte)inData.ReadByte(); ml = (byte)inData.ReadByte(); mh = (byte)inData.ReadByte(); hi = (byte)inData.ReadByte();
                            R2 = (uint)(lo | ml << 8 | mh << 16 | hi << 24);
                            break;

                        default:
                            return -1;
                    }
                }

                if (inData.Position > (startpos + inLen))
                {
                    if (inData.Position > (startpos + inLen + 2) || bitbuf.GetBitsLeft() < 16) return -1;
                }

                while ((this_run = (int)m_state.block_remaining) > 0 && togo > 0)
                {
                    if (this_run > togo) this_run = togo;
                    togo -= this_run;
                    m_state.block_remaining -= (uint)this_run;

                    window_posn &= window_size - 1;
                    if ((window_posn + this_run) > window_size)
                        return -1;

                    switch (m_state.block_type)
                    {
                        case LzxConstants.BLOCKTYPE.VERBATIM:
                            while (this_run > 0)
                            {
                                main_element = (int)ReadHuffSym(m_state.MAINTREE_table, m_state.MAINTREE_len,
                                                           LzxConstants.MAINTREE_MAXSYMBOLS, LzxConstants.MAINTREE_TABLEBITS,
                                                           bitbuf);
                                if (main_element < LzxConstants.NUM_CHARS)
                                {
                                    window[window_posn++] = (byte)main_element;
                                    this_run--;
                                }
                                else
                                {
                                    main_element -= LzxConstants.NUM_CHARS;

                                    match_length = main_element & LzxConstants.NUM_PRIMARY_LENGTHS;
                                    if (match_length == LzxConstants.NUM_PRIMARY_LENGTHS)
                                    {
                                        length_footer = (int)ReadHuffSym(m_state.LENGTH_table, m_state.LENGTH_len,
                                                                    LzxConstants.LENGTH_MAXSYMBOLS, LzxConstants.LENGTH_TABLEBITS,
                                                                    bitbuf);
                                        match_length += length_footer;
                                    }
                                    match_length += LzxConstants.MIN_MATCH;

                                    match_offset = main_element >> 3;

                                    if (match_offset > 2)
                                    {
                                        if (match_offset != 3)
                                        {
                                            extra = extra_bits[match_offset];
                                            verbatim_bits = (int)bitbuf.ReadBits((byte)extra);
                                            match_offset = (int)position_base[match_offset] - 2 + verbatim_bits;
                                        }
                                        else
                                        {
                                            match_offset = 1;
                                        }

                                        R2 = R1; R1 = R0; R0 = (uint)match_offset;
                                    }
                                    else if (match_offset == 0)
                                    {
                                        match_offset = (int)R0;
                                    }
                                    else if (match_offset == 1)
                                    {
                                        match_offset = (int)R1;
                                        R1 = R0; R0 = (uint)match_offset;
                                    }
                                    else
                                    {
                                        match_offset = (int)R2;
                                        R2 = R0; R0 = (uint)match_offset;
                                    }

                                    rundest = (int)window_posn;
                                    this_run -= match_length;

                                    if (window_posn >= match_offset)
                                    {
                                        runsrc = rundest - match_offset;
                                    }
                                    else
                                    {
                                        runsrc = rundest + ((int)window_size - match_offset);
                                        copy_length = match_offset - (int)window_posn;
                                        if (copy_length < match_length)
                                        {
                                            match_length -= copy_length;
                                            window_posn += (uint)copy_length;
                                            while (copy_length-- > 0) window[rundest++] = window[runsrc++];
                                            runsrc = 0;
                                        }
                                    }
                                    window_posn += (uint)match_length;

                                    while (match_length-- > 0) window[rundest++] = window[runsrc++];
                                }
                            }
                            break;

                        case LzxConstants.BLOCKTYPE.ALIGNED:
                            while (this_run > 0)
                            {
                                main_element = (int)ReadHuffSym(m_state.MAINTREE_table, m_state.MAINTREE_len,
                                                                          LzxConstants.MAINTREE_MAXSYMBOLS, LzxConstants.MAINTREE_TABLEBITS,
                                                                          bitbuf);

                                if (main_element < LzxConstants.NUM_CHARS)
                                {
                                    window[window_posn++] = (byte)main_element;
                                    this_run--;
                                }
                                else
                                {
                                    main_element -= LzxConstants.NUM_CHARS;

                                    match_length = main_element & LzxConstants.NUM_PRIMARY_LENGTHS;
                                    if (match_length == LzxConstants.NUM_PRIMARY_LENGTHS)
                                    {
                                        length_footer = (int)ReadHuffSym(m_state.LENGTH_table, m_state.LENGTH_len,
                                                                        LzxConstants.LENGTH_MAXSYMBOLS, LzxConstants.LENGTH_TABLEBITS,
                                                                        bitbuf);
                                        match_length += length_footer;
                                    }
                                    match_length += LzxConstants.MIN_MATCH;

                                    match_offset = main_element >> 3;

                                    if (match_offset > 2)
                                    {
                                        extra = extra_bits[match_offset];
                                        match_offset = (int)position_base[match_offset] - 2;
                                        if (extra > 3)
                                        {
                                            extra -= 3;
                                            verbatim_bits = (int)bitbuf.ReadBits((byte)extra);
                                            match_offset += (verbatim_bits << 3);
                                            aligned_bits = (int)ReadHuffSym(m_state.ALIGNED_table, m_state.ALIGNED_len,
                                                                       LzxConstants.ALIGNED_MAXSYMBOLS, LzxConstants.ALIGNED_TABLEBITS,
                                                                       bitbuf);
                                            match_offset += aligned_bits;
                                        }
                                        else if (extra == 3)
                                        {
                                            aligned_bits = (int)ReadHuffSym(m_state.ALIGNED_table, m_state.ALIGNED_len,
                                                                       LzxConstants.ALIGNED_MAXSYMBOLS, LzxConstants.ALIGNED_TABLEBITS,
                                                                       bitbuf);
                                            match_offset += aligned_bits;
                                        }
                                        else if (extra > 0)
                                        {
                                            verbatim_bits = (int)bitbuf.ReadBits((byte)extra);
                                            match_offset += verbatim_bits;
                                        }
                                        else
                                        {
                                            match_offset = 1;
                                        }

                                        R2 = R1; R1 = R0; R0 = (uint)match_offset;
                                    }
                                    else if (match_offset == 0)
                                    {
                                        match_offset = (int)R0;
                                    }
                                    else if (match_offset == 1)
                                    {
                                        match_offset = (int)R1;
                                        R1 = R0; R0 = (uint)match_offset;
                                    }
                                    else
                                    {
                                        match_offset = (int)R2;
                                        R2 = R0; R0 = (uint)match_offset;
                                    }

                                    rundest = (int)window_posn;
                                    this_run -= match_length;

                                    if (window_posn >= match_offset)
                                    {
                                        runsrc = rundest - match_offset;
                                    }
                                    else
                                    {
                                        runsrc = rundest + ((int)window_size - match_offset);
                                        copy_length = match_offset - (int)window_posn;
                                        if (copy_length < match_length)
                                        {
                                            match_length -= copy_length;
                                            window_posn += (uint)copy_length;
                                            while (copy_length-- > 0) window[rundest++] = window[runsrc++];
                                            runsrc = 0;
                                        }
                                    }
                                    window_posn += (uint)match_length;

                                    while (match_length-- > 0) window[rundest++] = window[runsrc++];
                                }
                            }
                            break;

                        case LzxConstants.BLOCKTYPE.UNCOMPRESSED:
                            if ((inData.Position + this_run) > endpos) return -1;
                            byte[] temp_buffer = new byte[this_run];
                            inData.Read(temp_buffer, 0, this_run);
                            temp_buffer.CopyTo(window, (int)window_posn);
                            window_posn += (uint)this_run;
                            break;

                        default:
                            return -1;
                    }
                }
            }

            if (togo != 0) return -1;
            int start_window_pos = (int)window_posn;
            if (start_window_pos == 0) start_window_pos = (int)window_size;
            start_window_pos -= outLen;
            outData.Write(window, start_window_pos, outLen);

            m_state.window_posn = window_posn;
            m_state.R0 = R0;
            m_state.R1 = R1;
            m_state.R2 = R2;

            if ((m_state.frames_read++ < 32768) && m_state.intel_filesize != 0)
            {
                if (outLen <= 6 || m_state.intel_started == 0)
                {
                    m_state.intel_curpos += outLen;
                }
                else
                {
                    int dataend = outLen - 10;
                    uint curpos = (uint)m_state.intel_curpos;

                    m_state.intel_curpos = (int)curpos + outLen;

                    while (outData.Position < dataend)
                    {
                        if (outData.ReadByte() != 0xE8) { curpos++; continue; }
                    }
                }
                return -1;
            }
            return 0;
        }

        private int MakeDecodeTable(uint nsyms, uint nbits, byte[] length, ushort[] table)
        {
            ushort sym;
            uint leaf;
            byte bit_num = 1;
            uint fill;
            uint pos = 0;
            uint table_mask = (uint)(1 << (int)nbits);
            uint bit_mask = table_mask >> 1;
            uint next_symbol = bit_mask;

            while (bit_num <= nbits)
            {
                for (sym = 0; sym < nsyms; sym++)
                {
                    if (length[sym] == bit_num)
                    {
                        leaf = pos;

                        if ((pos += bit_mask) > table_mask) return 1;

                        fill = bit_mask;
                        while (fill-- > 0) table[leaf++] = sym;
                    }
                }
                bit_mask >>= 1;
                bit_num++;
            }

            if (pos != table_mask)
            {
                for (sym = (ushort)pos; sym < table_mask; sym++) table[sym] = 0;

                pos <<= 16;
                table_mask <<= 16;
                bit_mask = 1 << 15;

                while (bit_num <= 16)
                {
                    for (sym = 0; sym < nsyms; sym++)
                    {
                        if (length[sym] == bit_num)
                        {
                            leaf = pos >> 16;
                            for (fill = 0; fill < bit_num - nbits; fill++)
                            {
                                if (table[leaf] == 0)
                                {
                                    table[(next_symbol << 1)] = 0;
                                    table[(next_symbol << 1) + 1] = 0;
                                    table[leaf] = (ushort)(next_symbol++);
                                }
                                leaf = (uint)(table[leaf] << 1);
                                if (((pos >> (int)(15 - fill)) & 1) == 1) leaf++;
                            }
                            table[leaf] = sym;

                            if ((pos += bit_mask) > table_mask) return 1;
                        }
                    }
                    bit_mask >>= 1;
                    bit_num++;
                }
            }

            if (pos == table_mask) return 0;

            for (sym = 0; sym < nsyms; sym++) if (length[sym] != 0) return 1;
            return 0;
        }

        private void ReadLengths(byte[] lens, uint first, uint last, BitBuffer bitbuf)
        {
            uint x, y;
            int z;

            for (x = 0; x < 20; x++)
            {
                y = bitbuf.ReadBits(4);
                m_state.PRETREE_len[x] = (byte)y;
            }
            MakeDecodeTable(LzxConstants.PRETREE_MAXSYMBOLS, LzxConstants.PRETREE_TABLEBITS,
                            m_state.PRETREE_len, m_state.PRETREE_table);

            for (x = first; x < last;)
            {
                z = (int)ReadHuffSym(m_state.PRETREE_table, m_state.PRETREE_len,
                                LzxConstants.PRETREE_MAXSYMBOLS, LzxConstants.PRETREE_TABLEBITS, bitbuf);
                if (z == 17)
                {
                    y = bitbuf.ReadBits(4); y += 4;
                    while (y-- != 0) lens[x++] = 0;
                }
                else if (z == 18)
                {
                    y = bitbuf.ReadBits(5); y += 20;
                    while (y-- != 0) lens[x++] = 0;
                }
                else if (z == 19)
                {
                    y = bitbuf.ReadBits(1); y += 4;
                    z = (int)ReadHuffSym(m_state.PRETREE_table, m_state.PRETREE_len,
                                LzxConstants.PRETREE_MAXSYMBOLS, LzxConstants.PRETREE_TABLEBITS, bitbuf);
                    z = lens[x] - z; if (z < 0) z += 17;
                    while (y-- != 0) lens[x++] = (byte)z;
                }
                else
                {
                    z = lens[x] - z; if (z < 0) z += 17;
                    lens[x++] = (byte)z;
                }
            }
        }

        private uint ReadHuffSym(ushort[] table, byte[] lengths, uint nsyms, uint nbits, BitBuffer bitbuf)
        {
            uint i, j;
            bitbuf.EnsureBits(16);
            if ((i = table[bitbuf.PeekBits((byte)nbits)]) >= nsyms)
            {
                j = (uint)(1 << (int)((sizeof(uint) * 8) - nbits));
                do
                {
                    j >>= 1; i <<= 1; i |= (bitbuf.GetBuffer() & j) != 0 ? (uint)1 : 0;
                    if (j == 0) return 0;
                } while ((i = table[i]) >= nsyms);
            }
            j = lengths[i];
            bitbuf.RemoveBits((byte)j);

            return i;
        }

        private class BitBuffer
        {
            uint buffer;
            byte bitsleft;
            Stream byteStream;

            public BitBuffer(Stream stream)
            {
                byteStream = stream;
                InitBitStream();
            }

            public void InitBitStream()
            {
                buffer = 0;
                bitsleft = 0;
            }

            public void EnsureBits(byte bits)
            {
                while (bitsleft < bits)
                {
                    int lo = (byte)byteStream.ReadByte();
                    int hi = (byte)byteStream.ReadByte();
                    buffer |= (uint)(((hi << 8) | lo) << (sizeof(uint) * 8 - 16 - bitsleft));
                    bitsleft += 16;
                }
            }

            public uint PeekBits(byte bits)
            {
                return (buffer >> ((sizeof(uint) * 8) - bits));
            }

            public void RemoveBits(byte bits)
            {
                buffer <<= bits;
                bitsleft -= bits;
            }

            public uint ReadBits(byte bits)
            {
                uint ret = 0;

                if (bits > 0)
                {
                    EnsureBits(bits);
                    ret = PeekBits(bits);
                    RemoveBits(bits);
                }

                return ret;
            }

            public uint GetBuffer()
            {
                return buffer;
            }

            public byte GetBitsLeft()
            {
                return bitsleft;
            }
        }

        private struct LzxState
        {
            public uint R0, R1, R2;
            public ushort main_elements;
            public int header_read;
            public LzxConstants.BLOCKTYPE block_type;
            public uint block_length;
            public uint block_remaining;
            public uint frames_read;
            public int intel_filesize;
            public int intel_curpos;
            public int intel_started;

            public ushort[] PRETREE_table;
            public byte[] PRETREE_len;
            public ushort[] MAINTREE_table;
            public byte[] MAINTREE_len;
            public ushort[] LENGTH_table;
            public byte[] LENGTH_len;
            public ushort[] ALIGNED_table;
            public byte[] ALIGNED_len;

            public uint actual_size;
            public byte[] window;
            public uint window_size;
            public uint window_posn;
        }
    }

    internal struct LzxConstants
    {
        public const ushort MIN_MATCH = 2;
        public const ushort MAX_MATCH = 257;
        public const ushort NUM_CHARS = 256;
        public enum BLOCKTYPE
        {
            INVALID = 0,
            VERBATIM = 1,
            ALIGNED = 2,
            UNCOMPRESSED = 3
        }
        public const ushort PRETREE_NUM_ELEMENTS = 20;
        public const ushort ALIGNED_NUM_ELEMENTS = 8;
        public const ushort NUM_PRIMARY_LENGTHS = 7;
        public const ushort NUM_SECONDARY_LENGTHS = 249;

        public const ushort PRETREE_MAXSYMBOLS = PRETREE_NUM_ELEMENTS;
        public const ushort PRETREE_TABLEBITS = 6;
        public const ushort MAINTREE_MAXSYMBOLS = NUM_CHARS + 50 * 8;
        public const ushort MAINTREE_TABLEBITS = 12;
        public const ushort LENGTH_MAXSYMBOLS = NUM_SECONDARY_LENGTHS + 1;
        public const ushort LENGTH_TABLEBITS = 12;
        public const ushort ALIGNED_MAXSYMBOLS = ALIGNED_NUM_ELEMENTS;
        public const ushort ALIGNED_TABLEBITS = 7;

        public const ushort LENTABLE_SAFETY = 64;
    }

    internal class UnsupportedWindowSizeRange : Exception
    {
    }
}
