﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using PeterO.Cbor;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Com.AugustCellars.COSE;

namespace Com.AugustCellars.CoAP.OSCOAP
{
#if INCLUDE_OSCOAP
    /// <summary>
    /// Security context information for use with the OSCOAP structures.
    /// This structure supports doing both unicast and multicast transmission and
    /// receiption of messages.
    /// </summary>
    public class SecurityContext
    {
        /// <summary>
        /// Class implementation used for doing checking if a message is being replayed at us.
        /// </summary>
        public class ReplayWindow
        {
            BitArray _hits;
            Int64 _baseValue;

            /// <summary>
            /// create a replaywindow and initialize where the floating window is.
            /// </summary>
            /// <param name="baseValue">Start value to check for hits</param>
            /// <param name="arraySize">Size of the replay window</param>
            public ReplayWindow(int baseValue, int arraySize)
            {
                _baseValue = baseValue;
                _hits = new BitArray(arraySize);
            }

            /// <summary>
            /// Check if the value is in the replay window and if it has been set.
            /// </summary>
            /// <param name="index">value to check</param>
            /// <returns>true if should treat as replay</returns>
            public bool HitTest(Int64 index)
            {
                index -= _baseValue;
                if (index < 0) return true;
                if (index > _hits.Length) return false;
                return _hits.Get((int)index);
            }

            /// <summary>
            /// Set a value has having been seen.
            /// </summary>
            /// <param name="index">value that was seen</param>
            public void SetHit(Int64 index)
            {
                index -= _baseValue;
                if (index < 0) return;
                if (index > _hits.Length) {
                    if (index < _hits.Length * 3 / 2) {
                        int v = _hits.Length / 2;
                        _baseValue += v;
                        BitArray t = new BitArray(_hits.Length);
                        for (int i = 0; i < v; i++) t[i] = _hits[i + v];
                        _hits = t;
                        index -= v;
                    }
                    else {
                        _baseValue = index;
                        _hits.SetAll(false);
                        index = 0;
                    }
                }
                _hits.Set((int) index, true);
            }
        }

        /// <summary>
        /// Crypto information dealing with a single entity that sends data
        /// </summary>
        public class EntityContext
        {
            /// <summary>
            /// Create new entity cyrpto context structure
            /// </summary>
            public EntityContext() { }

            /// <summary>
            /// Create new entity cyrpto context structure
            /// Copy constructor - needed to clone key material
            /// </summary>
            /// <param name="old">old structure</param>
            public EntityContext(EntityContext old)
            {
                Algorithm = old.Algorithm;
                BaseIV = (byte[])old.BaseIV.Clone();
                Key = (byte[])old.Key.Clone();
                Id = (byte[])old.Id.Clone();
                ReplayWindow = new ReplayWindow(0, 256);
                SequenceNumber = old.SequenceNumber;
                SigningKey = old.SigningKey;
            }

            /// <summary>
            /// What encrption algorithm is being used?
            /// </summary>
            public CBORObject Algorithm { get; set; }

            /// <summary>
            /// What is the base IV value for this context?
            /// </summary>
            public byte[] BaseIV { get; set; }

            /// <summary>
            /// What is the identity of this context - matches a key identifier.
            /// </summary>
            public byte[] Id { get; set; }

            /// <summary>
            /// What is the cryptographic key?
            /// </summary>
            public byte[] Key { get; set; }

            /// <summary>
            /// What is the current sequence number (IV) for the context?
            /// </summary>
            public int SequenceNumber { get; set; }

            /// <summary>
            /// Return the sequence number as a byte array.
            /// </summary>
            public byte[] PartialIV
            {
                get
                {
                    byte[] part = BitConverter.GetBytes(SequenceNumber);
                    if (BitConverter.IsLittleEndian) Array.Reverse(part);
                    int i;
                    for (i = 0; i < part.Length - 1; i++) if (part[i] != 0) break;
                    Array.Copy(part, i, part, 0, part.Length - i);
                    Array.Resize(ref part, part.Length - i);

                    return part;
                }
            }

            /// <summary>
            /// Given a partial IV, create the actual IV to use
            /// </summary>
            /// <param name="partialIV">partial IV</param>
            /// <returns>full IV</returns>
            public CBORObject GetIV(CBORObject partialIV)
            {
                return GetIV(partialIV.GetByteString());
            }

            /// <summary>
            /// Given a partial IV, create the actual IV to use
            /// </summary>
            /// <param name="partialIV">partial IV</param>
            /// <returns>full IV</returns>
            public CBORObject GetIV(byte[] partialIV)
            {
                byte[] iv = (byte[])BaseIV.Clone();
                int offset = iv.Length - partialIV.Length;

                for (int i = 0; i < partialIV.Length; i++) iv[i + offset] ^= partialIV[i];

                return CBORObject.FromObject(iv);
            }

            /// <summary>
            /// Get/Set the replay window checker for the context.
            /// </summary>
            public ReplayWindow ReplayWindow { get; set; }

            /// <summary>
            /// Increment the sequence/parital IV
            /// </summary>
            public void IncrementSequenceNumber() { SequenceNumber += 1; }

            /// <summary>
            /// The key to use for counter signing purposes
            /// </summary>
            public OneKey SigningKey { get; set; }
        }

        private static int _ContextNumber;
        private byte[] _masterSecret;
        private byte[] _salt;

        /// <summary>
        /// What is the global unique context number for this context.
        /// </summary>
        public int ContextNo { get; private set; }

        /// <summary>
        /// Return the sender information object
        /// </summary>
        public EntityContext Sender { get; private set; } = new EntityContext();

        /// <summary>
        /// Return the single receipient object
        /// </summary>
        public EntityContext Recipient { get; private set; }

        /// <summary>
        /// Get the set of all recipients for group.
        /// </summary>
        public Dictionary<byte[], EntityContext> Recipients { get; private set; } = new Dictionary<byte[], EntityContext>();

        /// <summary>
        /// Group ID for multi-cast.
        /// </summary>
        public byte[] GroupId { get; set; }

        /// <summary>
        /// Location for a user to place significant information.
        /// For contexts that are created by the system this will be a list of
        /// COSE Web Tokens for authorization
        /// </summary>
        public Object UserData { get; set; }

        /// <summary>
        /// Create a new empty security context
        /// </summary>
        public SecurityContext() { }

        /// <summary>
        /// Create a new security context to hold info for group.
        /// </summary>
        /// <param name="groupId"></param>
        public SecurityContext(byte[] groupId)
        {
            Recipients = new Dictionary<byte[], EntityContext>(new ByteArrayComparer());
            GroupId = groupId;
        }

        /// <summary>
        /// Clone a security context - needed because key info needs to be copied.
        /// </summary>
        /// <param name="old">context to clone</param>
        public SecurityContext(SecurityContext old)
        {
            ContextNo = old.ContextNo;
            GroupId = old.GroupId;
            Sender = new EntityContext(old.Sender);
            if (old.Recipient != null) Recipient = new EntityContext(old.Recipient);
            if (old.Recipients != null) {
                Recipients = new Dictionary<byte[], EntityContext>(new ByteArrayComparer());
                foreach (var item in old.Recipients) {
                    Recipients[item.Key] = new EntityContext(new EntityContext(item.Value));
                }
            }
        }



        public void AddRecipient(byte[] recipientId, OneKey signKey)
        {
            EntityContext x = DeriveEntityContext(_masterSecret, null, recipientId, _salt, Sender.Algorithm, null);
            x.SigningKey = signKey;

            Recipients.Add(recipientId, x);
        }


        /// <summary>
        /// Given the set of inputs, perform the crptographic operations that are needed
        /// to build a security context for a single sender and recipient.
        /// </summary>
        /// <param name="masterSecret">pre-shared key</param>
        /// <param name="senderId">name assigned to sender</param>
        /// <param name="recipientId">name assigned to recipient</param>
        /// <param name="masterSalt">salt value</param>
        /// <param name="algAEAD">encryption algorithm</param>
        /// <param name="algKeyAgree">key agreement algorithm</param>
        /// <returns></returns>
        public static SecurityContext DeriveContext(byte[] masterSecret, byte[] senderId, byte[] recipientId, byte[] masterSalt = null, CBORObject algAEAD = null, CBORObject algKeyAgree = null)
        {
            int cbKey;
            int cbIV;
            SecurityContext ctx = new SecurityContext();

            if (algAEAD == null) ctx.Sender.Algorithm = AlgorithmValues.AES_CCM_16_64_128;
            else ctx.Sender.Algorithm = algAEAD;

            if (ctx.Sender.Algorithm.Type != CBORType.Number) throw new Exception("Unsupported algorithm");
            switch ((AlgorithmValuesInt) ctx.Sender.Algorithm.AsInt32()) {
                case AlgorithmValuesInt.AES_CCM_16_64_128:
                    cbKey = 128/8;
                    cbIV = 13;
                    break;

                case AlgorithmValuesInt.AES_CCM_64_64_128:
                    cbKey = 128/8;
                    cbIV = 56/8;
                    break;

                case AlgorithmValuesInt.AES_CCM_64_128_128:
                    cbKey = 128 / 8;
                    cbIV = 56 / 8;
                    break;

                case AlgorithmValuesInt.AES_CCM_16_128_128:
                    cbKey = 128 / 8;
                    cbIV = 13;
                    break;

                case AlgorithmValuesInt.AES_GCM_128:
                    cbKey = 128 / 8;
                    cbIV = 96 / 8;
                    break;

                default:
                    throw new Exception("Unsupported algorithm");
            }

            ctx.Sender.Id = senderId ?? throw new ArgumentNullException(nameof(senderId));

            ctx.Recipient = new EntityContext {
                Algorithm = ctx.Sender.Algorithm,
                Id = recipientId ?? throw new ArgumentNullException(nameof(recipientId)),
                ReplayWindow = new ReplayWindow(0, 64)
            };


            CBORObject info = CBORObject.NewArray();

            info.Add(senderId); // 0
            info.Add(CBORObject.Null); // 1
            info.Add(ctx.Sender.Algorithm); // 2
            info.Add("Key"); // 3
            info.Add(cbKey); // 4 in bytes

            IDigest sha256;

            if (algKeyAgree == null || algKeyAgree.Equals(AlgorithmValues.ECDH_SS_HKDF_256)) {

                sha256 = new Sha256Digest();
            }
            else if (algKeyAgree.Equals(AlgorithmValues.ECDH_SS_HKDF_512)) {
                sha256 = new Sha512Digest();
            }
            else throw new ArgumentException("Unrecognized key agreement algorithm");

            IDerivationFunction hkdf = new HkdfBytesGenerator(sha256);
            hkdf.Init(new HkdfParameters(masterSecret, masterSalt, info.EncodeToBytes()));

            ctx.Sender.Key = new byte[cbKey];
            hkdf.GenerateBytes(ctx.Sender.Key, 0, ctx.Sender.Key.Length);

            info[0] = CBORObject.FromObject(recipientId);
            hkdf.Init(new HkdfParameters(masterSecret, masterSalt, info.EncodeToBytes()));
            ctx.Recipient.Key = new byte[cbKey];
            hkdf.GenerateBytes(ctx.Recipient.Key, 0, ctx.Recipient.Key.Length);

            info[0] = CBORObject.FromObject(new byte[0]);
            info[3] = CBORObject.FromObject("IV");
            info[4] = CBORObject.FromObject(cbIV);
            hkdf.Init(new HkdfParameters(masterSecret, masterSalt, info.EncodeToBytes()));
            ctx.Recipient.BaseIV = new byte[cbIV];
            hkdf.GenerateBytes(ctx.Recipient.BaseIV, 0, ctx.Recipient.BaseIV.Length);
            ctx.Sender.BaseIV = (byte[]) ctx.Recipient.BaseIV.Clone();

            int iIv = cbIV - 5 - senderId.Length;
            if (cbIV - 6 < senderId.Length) throw new Exception("Sender Id too long");
            ctx.Sender.BaseIV[0] ^= (byte) senderId.Length;
            for (int i = 0; i < senderId.Length; i++) ctx.Sender.BaseIV[iIv + i] ^= senderId[i];

            iIv = cbIV - 5 - recipientId.Length;
            if (cbIV - 6 < recipientId.Length) throw new Exception("Recipient Id too long");
            ctx.Recipient.BaseIV[0] ^= (byte) recipientId.Length;
            for (int i = 0; i < recipientId.Length; i++) ctx.Recipient.BaseIV[iIv + i] ^= recipientId[i];

            //  Give a unique context number for doing comparisons

            ctx.ContextNo = _ContextNumber;
            _ContextNumber += 1;

            return ctx;
        }

        /// <summary>
        /// Given the set of inputs, perform the crptographic operations that are needed
        /// to build a security context for a single sender and recipient.
        /// </summary>
        /// <param name="masterSecret">pre-shared key</param>
        /// <param name="senderId">name assigned to sender</param>
        /// <param name="recipientIds">names assigned to recipients</param>
        /// <param name="masterSalt">salt value</param>
        /// <param name="algAEAD">encryption algorithm</param>
        /// <param name="algKeyAgree">key agreement algorithm</param>
        /// <returns></returns>
        public static SecurityContext DeriveGroupContext(byte[] masterSecret, byte[] groupID, byte[] senderId, byte[][] recipientIds, byte[] masterSalt = null, CBORObject algAEAD = null, CBORObject algKeyAgree = null)
        {
            SecurityContext ctx = new SecurityContext();
            ctx._masterSecret = masterSecret;
            ctx._salt = masterSalt;

            ctx.Sender = DeriveEntityContext(masterSecret, groupID, senderId, masterSalt, algAEAD, algKeyAgree);
            if (recipientIds != null) {
                ctx.Recipients = new Dictionary<byte[], EntityContext>(new ByteArrayComparer());
                foreach (byte[] id in recipientIds) {
                    ctx.Recipients.Add(id, DeriveEntityContext(masterSecret, groupID, id, masterSalt, algAEAD, algKeyAgree));
                }
            }

            ctx.GroupId = groupID;

            return ctx;
        }

        /// <summary>
        /// Given the set of inputs, perform the crptographic operations that are needed
        /// to build a security context for a single sender and recipient.
        /// </summary>
        /// <param name="masterSecret">pre-shared key</param>
        /// <param name="entityId">name assigned to sender</param>
        /// <param name="masterSalt">salt value</param>
        /// <param name="algAEAD">encryption algorithm</param>
        /// <param name="algKeyAgree">key agreement algorithm</param>
        /// <returns></returns>
        private static EntityContext DeriveEntityContext(byte[] masterSecret, byte[] groupId, byte[] entityId, byte[] masterSalt = null, CBORObject algAEAD = null, CBORObject algKeyAgree = null)
        {
            EntityContext ctx = new EntityContext();
            int keySize;
            int ivSize;

            ctx.Algorithm = algAEAD ?? AlgorithmValues.AES_CCM_16_64_128;
            ctx.Id = entityId ?? throw new ArgumentNullException(nameof(entityId));
            if (algKeyAgree == null) algKeyAgree = AlgorithmValues.ECDH_SS_HKDF_256;

            if (ctx.Algorithm.Type != CBORType.Number) throw new ArgumentException("algorithm is unknown" );
            switch ((AlgorithmValuesInt) ctx.Algorithm.AsInt32()) {
                case AlgorithmValuesInt.AES_CCM_16_64_128:
                    keySize = 128 / 8;
                    ivSize = 13;
                    break;

                case AlgorithmValuesInt.AES_GCM_128:
                    keySize = 128 / 8;
                    ivSize = 96 / 8;
                    break;                       

                default:
                    throw new ArgumentException("algorithm is unknown");
            }

            ctx.ReplayWindow = new ReplayWindow(0, 64);

            CBORObject info = CBORObject.NewArray();

            // M00TODO - add the group id into this

            info.Add(entityId);                 // 0
            info.Add(groupId);                  // 1
            info.Add(ctx.Algorithm);            // 2
            info.Add("Key");                    // 3
            info.Add(keySize);                  // 4 in bytes


            IDigest sha256;
            
            if (algKeyAgree == null || algKeyAgree.Equals(AlgorithmValues.ECDH_SS_HKDF_256)) {
                sha256 = new Sha256Digest();
            }
            else if (algKeyAgree.Equals(AlgorithmValues.ECDH_SS_HKDF_512)) {
                sha256 = new Sha512Digest();
            }
            else throw new ArgumentException("Unknown key agree algorithm");

            IDerivationFunction hkdf = new HkdfBytesGenerator(sha256);
            hkdf.Init(new HkdfParameters(masterSecret, masterSalt, info.EncodeToBytes()));

            ctx.Key = new byte[keySize];
            hkdf.GenerateBytes(ctx.Key, 0, ctx.Key.Length);

            info[0] = CBORObject.FromObject(new byte[0]);
            info[3] = CBORObject.FromObject("IV");
            info[4] = CBORObject.FromObject(ivSize);
            hkdf.Init(new HkdfParameters(masterSecret, masterSalt, info.EncodeToBytes()));
            ctx.BaseIV = new byte[ivSize];
            hkdf.GenerateBytes(ctx.BaseIV, 0, ctx.BaseIV.Length);

            // Modify the context 

            if (ivSize - 6 < entityId.Length) throw new Exception("Entity id is too long");
            ctx.BaseIV[0] ^= (byte) entityId.Length;
            int i1 = ivSize - 5 - entityId.Length /*- 1*/;
            for (int i = 0; i < entityId.Length; i++) ctx.BaseIV[i1 + i] ^= entityId[i];

            return ctx;
        }




#if DEBUG
        static public int FutzError { get; set; }
#endif

        public class ByteArrayComparer : EqualityComparer<byte[]>
        {
            public override bool Equals(byte[] first, byte[] second)
            {
                if (first == null || second == null) {
                    // null == null returns true.
                    // non-null == null returns false.
                    return first == second;
                }
                if (ReferenceEquals(first, second)) {
                    return true;
                }
                if (first.Length != second.Length) {
                    return false;
                }
                // Linq extension method is based on IEnumerable, must evaluate every item.
                return first.SequenceEqual(second);
            }
            public override int GetHashCode(byte[] obj)
            {
                if (obj == null) {
                    throw new ArgumentNullException(nameof(obj));
                }
                // quick and dirty, instantly identifies obviously different
                // arrays as being different
                return obj.Length;
            }
        }
    }
#endif
}
