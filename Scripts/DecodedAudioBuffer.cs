﻿/*
 * AudioDecodingBuffer
 * Receives decoded audio buffers, and copies them into the
 * array passed via Read()
 */
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using System.Threading;

namespace Mumble {
    public class AudioDecodingBuffer : IDisposable
    {
        public long NumPacketsLost { get; private set; }
        public bool HasFilledInitialBuffer { get; private set; }
        /// <summary>
        /// How many samples have been decoded
        /// </summary>
        private int _decodedCount;
        private DecodedPacket _currentPacket;
        /// <summary>
        /// Name of the speaker being decoded
        /// Only used for debugging
        /// </summary>
        private string _name;
        private UInt32 _session;

        private readonly AudioDecodeThread _audioDecodeThread;
        private readonly object _bufferLock = new object();
        private readonly Queue<DecodedPacket> _decodedBuffer = new Queue<DecodedPacket>();

        /// <summary>
        /// How many incoming packets to buffer before audio begins to be played
        /// Higher values increase stability and latency
        /// </summary>
        const int InitialSampleBuffer = 3;

        public AudioDecodingBuffer(AudioDecodeThread audioDecodeThread)
        {
            _audioDecodeThread = audioDecodeThread;
        }
        public void Init(string name, UInt32 session)
        {
            Debug.Log("Init decoding buffer for: " + name);
            _name = name;
            _session = session;
            _audioDecodeThread.AddDecoder(_session);
        }
        public int Read(float[] buffer, int offset, int count)
        {
            // Don't send audio until we've filled our initial buffer of packets
            if (!HasFilledInitialBuffer)
            {
                Array.Clear(buffer, offset, count);
                //Debug.Log("this should not happen");
                return 0;
            }

            //lock (_bufferLock)
            //{
                //Debug.Log("We now have " + _decodedBuffer.Count + " decoded packets");
            //}
            //Debug.LogWarning("Will read");

            int readCount = 0;
            while (readCount < count && _decodedCount > 0)
                readCount += ReadFromBuffer(buffer, offset + readCount, count - readCount);
            //Debug.Log("Read: " + readCount + " #decoded: " + _decodedCount);

            //Return silence if there was no data available
            if (readCount == 0)
            {
                //Debug.Log("Returning silence");
                Array.Clear(buffer, offset, count);
            } else if (readCount < count)
            {
                //Debug.LogWarning("Buffer underrun: " + (count - readCount) + " samples. Asked: " + count + " provided: " + readCount + " numDec: " + _decodedCount);
                Array.Clear(buffer, offset + readCount, count - readCount);
            }
            
            return readCount;
        }

        /// <summary>
        /// Read data that has already been decoded
        /// </summary>
        /// <param name="dst"></param>
        /// <param name="dstOffset"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        private int ReadFromBuffer(float[] dst, int dstOffset, int count)
        {
            // Get the next DecodedPacket to use
            int numInSample = _currentPacket.PcmLength - _currentPacket.ReadOffset;
            if(numInSample == 0)
            {
                lock (_bufferLock)
                {
                    if(_decodedBuffer.Count == 0)
                    {
                        Debug.LogError("No available decode buffers!");
                        return 0;
                    }
                    _currentPacket = _decodedBuffer.Dequeue();
                }
                numInSample = _currentPacket.PcmLength - _currentPacket.ReadOffset;
            }

            int readCount = Math.Min(numInSample, count);
            Array.Copy(_currentPacket.PcmData, _currentPacket.ReadOffset, dst, dstOffset, readCount);
            //Debug.Log("We have: "
                //+ _decodedCount + " samples decoded "
                //+ numInSample + " samples in curr packet "
                //+ _currentPacket.ReadOffset + " read offset "
                //+ readCount + " numRead");

            Interlocked.Add(ref _decodedCount, -readCount);
            _currentPacket.ReadOffset += readCount;
            return readCount;
        }

        internal void AddDecodedAudio(float[] pcmData, int pcmLength, bool reevaluateInitialBuffer)
        {
            DecodedPacket decodedPacket = new DecodedPacket
            {
                PcmData = pcmData,
                PcmLength = pcmLength
            };

            //if (reevaluateInitialBuffer)
                //Debug.Log("Will refill our initial buffer");

            int count = 0;
            lock (_bufferLock)
            {
                count = _decodedBuffer.Count;
                if(count > MumbleConstants.RECEIVED_PACKET_BUFFER_SIZE)
                {
                    // TODO this seems to happen at times
                    Debug.LogWarning("Max recv buffer size reached, dropping for user " + _name);
                    return;
                }

                _decodedBuffer.Enqueue(decodedPacket);
                Interlocked.Add(ref _decodedCount, pcmLength);

                // this is set if the previous received packet was a last packet
                // or if there was an abrupt change in sequence number
                if (reevaluateInitialBuffer)
                    HasFilledInitialBuffer = false;

                if (!HasFilledInitialBuffer && (count + 1 >= InitialSampleBuffer))
                    HasFilledInitialBuffer = true;
            }

            //Debug.Log("Adding " + pcmLength + " num packets: " + count + " total decoded: " + _decodedCount);
        }

        public void Reset()
        {
            lock (_bufferLock)
            {
                _name = null;
                _audioDecodeThread.RemoveDecoder(_session);
                NumPacketsLost = 0;
                HasFilledInitialBuffer = false;
                _decodedCount = 0;
                _decodedBuffer.Clear();
                _currentPacket = new DecodedPacket{ };
                _session = 0;
            }
        }
        public void Dispose()
        {
        }

        private struct DecodedPacket
        {
            public float[] PcmData;
            public int PcmLength;
            public int ReadOffset;
            //public long Sequence;
            //public bool IsLast;
        }
    }
}