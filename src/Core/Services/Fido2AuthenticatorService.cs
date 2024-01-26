﻿using Bit.Core.Abstractions;
using Bit.Core.Models.View;
using Bit.Core.Enums;
using Bit.Core.Models.Domain;
using Bit.Core.Utilities.Fido2;
using Bit.Core.Utilities;

namespace Bit.Core.Services
{
    public class Fido2AuthenticatorService : IFido2AuthenticatorService
    {
        private INativeLogService _logService;
        private ICipherService _cipherService;
        private ISyncService _syncService;
        private ICryptoFunctionService _cryptoFunctionService;
        private IFido2UserInterface _userInterface;
        
        public Fido2AuthenticatorService(INativeLogService logService, ICipherService cipherService, ISyncService syncService, ICryptoFunctionService cryptoFunctionService, IFido2UserInterface userInterface)
        {
            _logService = logService;
            _cipherService = cipherService;
            _syncService = syncService;
            _cryptoFunctionService = cryptoFunctionService;
            _userInterface = userInterface;
        }

        public async Task<Fido2AuthenticatorMakeCredentialResult> MakeCredentialAsync(Fido2AuthenticatorMakeCredentialParams makeCredentialParams) 
        {
            if (makeCredentialParams.CredTypesAndPubKeyAlgs.All((p) => p.Algorithm != (int) Fido2AlgorithmIdentifier.ES256))
            {
                var requestedAlgorithms = string.Join(", ", makeCredentialParams.CredTypesAndPubKeyAlgs.Select((p) => p.Algorithm).ToArray());
                _logService.Warning(
                    $"[Fido2Authenticator] No compatible algorithms found, RP requested: {requestedAlgorithms}"
                );
                throw new NotSupportedError();
            }

            // await _userInterface.EnsureUnlockedVault();
            await _syncService.FullSyncAsync(false);

            var existingCipherIds = await FindExcludedCredentials(
                makeCredentialParams.ExcludeCredentialDescriptorList
            );
            if (existingCipherIds.Length > 0) {
                _logService.Info(
                    "[Fido2Authenticator] Aborting due to excluded credential found in vault."
                );
                await _userInterface.InformExcludedCredential(existingCipherIds);
                throw new NotAllowedError();
            }

            var response = await _userInterface.ConfirmNewCredentialAsync(new Fido2ConfirmNewCredentialParams {
                CredentialName = makeCredentialParams.RpEntity.Name,
                UserName = makeCredentialParams.UserEntity.Name,
                UserVerification = makeCredentialParams.RequireUserVerification
            });

            var cipherId = response.CipherId;
            var userVerified = response.UserVerified;
            string credentialId;
            if (cipherId == null) {
                _logService.Info(
                    "[Fido2Authenticator] Aborting because user confirmation was not recieved."
                );
                throw new NotAllowedError();
            }
            
            try {
                var (publicKey, privateKey) = await _cryptoFunctionService.EcdsaGenerateKeyPairAsync(CryptoEcdsaAlgorithm.P256Sha256);
                var fido2Credential = CreateCredentialView(makeCredentialParams, privateKey);

                var encrypted = await _cipherService.GetAsync(cipherId);
                var cipher = await encrypted.DecryptAsync();

                if (!userVerified && (makeCredentialParams.RequireUserVerification || cipher.Reprompt != CipherRepromptType.None)) {
                    _logService.Info(
                        "[Fido2Authenticator] Aborting because user verification was unsuccessful."
                    );
                    throw new NotAllowedError();
                }

                cipher.Login.Fido2Credentials = [fido2Credential];
                var reencrypted = await _cipherService.EncryptAsync(cipher);
                await _cipherService.SaveWithServerAsync(reencrypted);
                credentialId = fido2Credential.CredentialId;
            } catch {
                throw;
                // throw new NotImplementedException();
            }

            return new Fido2AuthenticatorMakeCredentialResult
            {
                CredentialId = GuidToRawFormat(credentialId),
                AttestationObject = Array.Empty<byte>(),
                AuthData = Array.Empty<byte>(),
                PublicKey = Array.Empty<byte>(),
                PublicKeyAlgorithm = (int) Fido2AlgorithmIdentifier.ES256,
            };
        }
        
        public async Task<Fido2AuthenticatorGetAssertionResult> GetAssertionAsync(Fido2AuthenticatorGetAssertionParams assertionParams)
        {
            List<CipherView> cipherOptions;

            // TODO: Unlock vault somehow
            // await userInterfaceSession.ensureUnlockedVault();
            await _syncService.FullSyncAsync(false);

            if (assertionParams.AllowCredentialDescriptorList?.Length > 0) {
                cipherOptions = await FindCredentialsById(
                    assertionParams.AllowCredentialDescriptorList,
                    assertionParams.RpId
                );
            } else {
                cipherOptions = await FindCredentialsByRp(assertionParams.RpId);
            }

            if (cipherOptions.Count == 0) {
                _logService.Info(
                    "[Fido2Authenticator] Aborting because no matching credentials were found in the vault."
                );

                throw new NotAllowedError();
            }

            var response = await _userInterface.PickCredentialAsync(new Fido2PickCredentialParams {
                CipherIds = cipherOptions.Select((cipher) => cipher.Id).ToArray(),
                UserVerification = assertionParams.RequireUserVerification
            });
            var selectedCipherId = response.CipherId;
            var userVerified = response.UserVerified;
            var selectedCipher = cipherOptions.FirstOrDefault((c) => c.Id == selectedCipherId);

            if (selectedCipher == null) {
                _logService.Info(
                    "[Fido2Authenticator] Aborting because the selected credential could not be found."
                );

                throw new NotAllowedError();
            }

            if (!userVerified && (assertionParams.RequireUserVerification || selectedCipher.Reprompt != CipherRepromptType.None)) {
                _logService.Info(
                    "[Fido2Authenticator] Aborting because user verification was unsuccessful."
                );

                throw new NotAllowedError();
            }
            
            try {
                var selectedFido2Credential = selectedCipher.Login.MainFido2Credential;
                var selectedCredentialId = selectedFido2Credential.CredentialId;

                if (selectedFido2Credential.CounterValue != 0) {
                    ++selectedFido2Credential.CounterValue;
                }

                await _cipherService.UpdateLastUsedDateAsync(selectedCipher.Id);
                var encrypted = await _cipherService.EncryptAsync(selectedCipher);
                await _cipherService.SaveWithServerAsync(encrypted);

                var authenticatorData = await GenerateAuthData(
                    rpId: selectedFido2Credential.RpId,
                    userPresence: true,
                    userVerification: userVerified,
                    counter: selectedFido2Credential.CounterValue
                );

                var signature = await GenerateSignature(
                    authData: authenticatorData,
                    clientDataHash: assertionParams.Hash,
                    privateKey: selectedFido2Credential.KeyBytes
                );

                return new Fido2AuthenticatorGetAssertionResult
                {
                    SelectedCredential = new Fido2AuthenticatorGetAssertionSelectedCredential
                    {
                        Id = GuidToRawFormat(selectedCredentialId),
                        UserHandle = selectedFido2Credential.UserHandleValue
                    },
                    AuthenticatorData = authenticatorData,
                    Signature = signature
                };
            } catch {
                _logService.Info(
                    "[Fido2Authenticator] Aborting because no matching credentials were found in the vault."
                );

                throw new UnknownError();
            }
        }

        ///<summary>
        /// Finds existing crendetials and returns the `CipherId` for each one
        ///</summary>
        private async Task<string[]> FindExcludedCredentials(
            PublicKeyCredentialDescriptor[] credentials
        ) {
            var ids = new List<string>();

            foreach (var credential in credentials) 
            {
                try
                {
                    ids.Add(GuidToStandardFormat(credential.Id));
                } catch {}
            }

            if (ids.Count == 0) {
                return [];
            }

            var ciphers = await _cipherService.GetAllDecryptedAsync();
            return ciphers
                .FindAll(
                    (cipher) =>
                    !cipher.IsDeleted &&
                    cipher.OrganizationId == null &&
                    cipher.Type == CipherType.Login &&
                    cipher.Login.HasFido2Credentials &&
                    ids.Contains(cipher.Login.MainFido2Credential.CredentialId)
                )
                .Select((cipher) => cipher.Id)
                .ToArray();
        }

        private async Task<List<CipherView>> FindCredentialsById(PublicKeyCredentialDescriptor[] credentials, string rpId)
        {
            var ids = new List<string>();

            foreach (var credential in credentials)
            {
                try
                {
                    ids.Add(GuidToStandardFormat(credential.Id));
                }
                catch {}
            }

            if (ids.Count == 0)
            {
                return new List<CipherView>();
            }

            var ciphers = await _cipherService.GetAllDecryptedAsync();
            return ciphers.FindAll((cipher) =>
                !cipher.IsDeleted &&
                cipher.Type == CipherType.Login &&
                cipher.Login.HasFido2Credentials &&
                cipher.Login.MainFido2Credential.RpId == rpId &&
                ids.Contains(cipher.Login.MainFido2Credential.CredentialId)
            );
        }

        private async Task<List<CipherView>> FindCredentialsByRp(string rpId)
        {
            var ciphers = await _cipherService.GetAllDecryptedAsync();
            return ciphers.FindAll((cipher) =>
                !cipher.IsDeleted &&
                cipher.Type == CipherType.Login &&
                cipher.Login.HasFido2Credentials &&
                cipher.Login.MainFido2Credential.RpId == rpId &&
                cipher.Login.MainFido2Credential.DiscoverableValue
            );
        }

        private Fido2CredentialView CreateCredentialView(Fido2AuthenticatorMakeCredentialParams makeCredentialsParams, byte[] privateKey)
        {
            return new Fido2CredentialView {
                CredentialId = Guid.NewGuid().ToString(),
                KeyType = "public-key",
                KeyAlgorithm = "ECDSA",
                KeyCurve = "P-256",
                KeyValue = CoreHelpers.Base64UrlEncode(privateKey),
                RpId = makeCredentialsParams.RpEntity.Id,
                UserHandle = CoreHelpers.Base64UrlEncode(makeCredentialsParams.UserEntity.Id),
                UserName = makeCredentialsParams.UserEntity.Name,
                CounterValue = 0,
                RpName = makeCredentialsParams.RpEntity.Name,
                // UserDisplayName = makeCredentialsParams.UserEntity.DisplayName,
                DiscoverableValue = makeCredentialsParams.RequireResidentKey,
                CreationDate = DateTime.Now
            };
        }

        private async Task<byte[]> GenerateAuthData(
            string rpId,
            bool userVerification,
            bool userPresence,
            int counter
            // byte[] credentialId,
            // CryptoKey? cryptoKey - only needed for attestation
        ) {
            List<byte> authData = new List<byte>();

            var rpIdHash = await _cryptoFunctionService.HashAsync(rpId, CryptoHashAlgorithm.Sha256);
            authData.AddRange(rpIdHash);

            var flags = AuthDataFlags(false, false, userVerification, userPresence);
            authData.Add(flags);

            authData.AddRange([
                (byte)(counter >> 24),
                (byte)(counter >> 16),
                (byte)(counter >> 8),
                (byte)counter
            ]);

            return authData.ToArray();
        }

        private byte AuthDataFlags(bool extensionData, bool attestationData, bool userVerification, bool userPresence) {
            byte flags = 0;

            if (extensionData) {
                flags |= 0b1000000;
            }

            if (attestationData) {
                flags |= 0b01000000;
            }

            if (userVerification) {
                flags |= 0b00000100;
            }

            if (userPresence) {
                flags |= 0b00000001;
            }

            return flags;
        }

        private async Task<byte[]> GenerateSignature(
            byte[] authData,
            byte[] clientDataHash,
            byte[] privateKey
        )
        {
            var sigBase = authData.Concat(clientDataHash).ToArray();
            var signature = await _cryptoFunctionService.SignAsync(sigBase, privateKey, new CryptoSignEcdsaOptions
            {
                Algorithm = CryptoEcdsaAlgorithm.P256Sha256,
                SignatureFormat = CryptoSignEcdsaOptions.DsaSignatureFormat.Rfc3279DerSequence
            });

            return signature;
        }

        private string GuidToStandardFormat(byte[] bytes)
        {
            return new Guid(bytes).ToString();
        }

        private byte[] GuidToRawFormat(string guid)
        {
            return Guid.Parse(guid).ToByteArray();
        }

    }
}
