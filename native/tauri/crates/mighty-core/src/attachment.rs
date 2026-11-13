//! C# `RunAttachment(Id, Name, MediaType, DataBase64)` (MightyCode.Core/AttachmentSupport.cs:9): a file sent with a request. Here so
//! the composer (which reads and checks them) and the run engine (which stages them) share one type. Its rules are
//! `mighty-composer`'s `attachments` module (C# `AttachmentSupport`).

use serde_json::Value;

use crate::json::{JsonOut, Obj, R, Wire, fail};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RunAttachment {
    pub id: String,
    pub name: String,
    pub media_type: String,
    pub data_base64: String,
}

impl Wire for RunAttachment {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "an attachment")?;
        let text = |name: &str| match o.get(name) {
            Some(Value::String(s)) => Ok(s.clone()),
            None | Some(Value::Null) => Ok(String::new()),
            Some(_) => fail(format!("attachment {name} is not a string")),
        };
        // AttachmentBase64Converter refuses a null or missing value (attachments.error.missingData).
        let data = match o.get("dataBase64") {
            Some(Value::String(s)) => s.clone(),
            _ => return fail("attachment dataBase64 is missing"),
        };
        Ok(RunAttachment { id: text("id")?, name: text("name")?, media_type: text("mediaType")?, data_base64: data })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("id", Some(&self.id));
        out.prop_str("name", Some(&self.name));
        out.prop_str("mediaType", Some(&self.media_type));
        // AttachmentBase64Converter writes the base64 alphabet as it is (UnsafeRelaxedJsonEscaping), so '+' is not +.
        out.name("dataBase64");
        let plain = self.data_base64.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'+' | b'/' | b'='));
        if plain {
            out.raw(&format!("\"{}\"", self.data_base64))
        } else {
            out.str(&self.data_base64)
        }
        out.end_object();
    }
}
