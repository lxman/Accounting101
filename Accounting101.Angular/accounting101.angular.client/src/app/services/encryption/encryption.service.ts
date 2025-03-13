import CryptoJS from 'crypto-js';

const SECRET_KEY = 'theultrasecretbestkeyintheworld7';
const SECRET_IV = '8394726593826159';

export function encrypt(plainText: string): string {
  const encrypted = CryptoJS.AES.encrypt(CryptoJS.enc.Utf8.parse(plainText), SECRET_KEY, {
      keySize: 128 / 8,
      iv: CryptoJS.enc.Utf8.parse(SECRET_IV),
      mode: CryptoJS.mode.CBC,
      padding: CryptoJS.pad.Pkcs7
  });
  return encrypted.toString();
}

// Decryption
export function decrypt(cipherText: string): string {
  const decrypted = CryptoJS.AES.decrypt(cipherText, SECRET_KEY, {
      keySize: 128 / 8,
      iv: CryptoJS.enc.Utf8.parse(SECRET_IV),
      mode: CryptoJS.mode.CBC,
      padding: CryptoJS.pad.Pkcs7
  });
  return decrypted.toString(CryptoJS.enc.Utf8);
}
