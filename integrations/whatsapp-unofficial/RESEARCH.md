# WhatsApp research

- Meta pricing: https://developers.facebook.com/documentation/business-messaging/whatsapp/pricing
  - Updated Sep 30, 2026; per-message, delivered messages, category + recipient country calling code.
  - Afghanistan (+93) is in Rest of Asia Pacific.
  - First 1,000 service messages per business phone/month free as of Oct 1, 2026; utility template receipts should not be assumed free.
  - Non-template messages only in open 24-hour window; outside it approved templates required.
- Meta send messages: https://developers.facebook.com/documentation/business-messaging/whatsapp/messages/send-messages
  - Service messages use Messages API; opt-in required; automation allowed in 24h window with escalation path.
- Meta policy: https://business.whatsapp.com/policy
  - opt-in required; business-initiated conversations require approved template; unauthorized at-scale messaging can lead to limits/removal.
- WhatsApp terms: https://www.whatsapp.com/legal/terms-of-service
  - prohibits impermissible bulk/auto messaging and unauthorized automated access; account may be suspended.
- Baileys repo disclaimer: https://github.com/WhiskeySockets/Baileys
  - unofficial/not affiliated; maintainers discourage bulk or automated messaging.
- whatsapp-web.js repo disclaimer: https://github.com/pedroslopez/whatsapp-web.js
  - unofficial, not affiliated; WhatsApp does not allow bots/unofficial clients; no guarantee against blocking.
- Third-party rate card cross-check (not authoritative): https://usewhapi.com/whatsapp-api-pricing/
  - Rest of Asia Pacific Utility/Service listed $0.0113/message effective Oct 1, 2026; 500 = $5.65 before taxes/provider fees.
