//! Source editor visibility and atomic replacement of a verified VBA project.
use crate::{CmdError, CmdResult, CommandSpec, Session, p};
use base64::Engine as _;
use serde_json::{Value, json};
use std::sync::Arc;

pub fn specs() -> Vec<CommandSpec> {
    vec![
        CommandSpec::new("vba.editor", "VBA Code Editor", "Developer › Code", |s, v| {
            s.view.vba_editor = p::bool(v, "value").unwrap_or(!s.view.vba_editor);
            Ok(json!({"value": s.view.vba_editor}))
        })
        .key("Alt+F11")
        .params(r#"{"value"?: bool}"#)
        .pure(),
        CommandSpec::new("vba.setProject", "Update VBA Project", "Developer › Code", set_project)
            .params(r#"{"project": base64 string, "document"?: document id, "revision"?: revision, "removeSignature"?: bool}"#),
    ]
}

fn set_project(s: &mut Session, v: &Value) -> CmdResult {
    if v.get("document").and_then(Value::as_u64).is_some_and(|id| id != s.document_id())
        || v.get("revision").and_then(Value::as_u64).is_some_and(|rev| rev != s.rev())
    {
        return Err(CmdError::Failed("The document changed while the VBA project was being prepared.".into()));
    }
    let encoded = p::str(v, "project").ok_or_else(|| CmdError::Params("project: base64 VBA project".into()))?;
    if encoded.len() > 24 * 1024 * 1024 {
        return Err(CmdError::Params("VBA project exceeds 16 MiB".into()));
    }
    let bytes = base64::engine::general_purpose::STANDARD.decode(encoded).map_err(|_| CmdError::Params("Invalid project encoding".into()))?;
    if bytes.len() > 16 * 1024 * 1024 || !bytes.starts_with(&[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]) {
        return Err(CmdError::Params("Expected a bounded compound-file VBA project".into()));
    }
    if s.doc.passthrough.get("word/vbaProject.bin").is_some_and(|old| old.as_slice() == bytes.as_slice()) {
        return Ok(json!({"updated":false}));
    }
    let manifest = s.doc.passthrough.get("wordcraft:vbaProject.related").map(|m| String::from_utf8_lossy(m).into_owned()).unwrap_or_default();
    let signed = manifest.lines().any(|line| line.split('\t').next().is_some_and(|kind| kind.to_ascii_lowercase().contains("signature")));
    if signed && !p::bool(v, "removeSignature").unwrap_or(false) {
        return Err(CmdError::Params("Editing signed VBA requires explicit removal of the invalidated signature.".into()));
    }
    let mut kept = String::new();
    for line in manifest.lines() {
        let fields: Vec<_> = line.split('\t').collect();
        if fields.first().is_some_and(|kind| kind.to_ascii_lowercase().contains("signature")) {
            if let Some(path) = fields.get(1) {
                s.doc.passthrough.remove(*path);
            }
        } else {
            kept.push_str(line);
            kept.push('\n');
        }
    }
    if !manifest.is_empty() {
        s.doc.passthrough.insert("wordcraft:vbaProject.related".into(), Arc::new(kept.into_bytes()));
    }
    s.doc.passthrough.insert("word/vbaProject.bin".into(), Arc::new(bytes));
    Ok(json!({"updated":true,"signatureRemoved":signed}))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn project_replacement_is_undoable_and_signed_changes_are_explicit() {
        let mut s = Session::new(wordcraft_doc::Document::from_text("Keep document text"));
        let old = vec![0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 1];
        s.doc.passthrough.insert("word/vbaProject.bin".into(), Arc::new(old.clone()));
        s.doc.passthrough.insert(
            "wordcraft:vbaProject.related".into(),
            Arc::new(b"vbaProjectSignature\tword/signature.bin\tsignature\nwordVbaData\tword/vbaData.xml\txml\n".to_vec()),
        );
        s.doc.passthrough.insert("word/signature.bin".into(), Arc::new(vec![9]));
        let mut updated = old.clone();
        updated.push(2);
        let project = base64::engine::general_purpose::STANDARD.encode(updated);
        assert!(s.run("vba.setProject", &json!({"project":project})).is_err());
        assert!(s.run("vba.setProject", &json!({"project":project,"document":s.document_id()+1})).is_err());
        s.run("vba.setProject", &json!({"project":project,"removeSignature":true})).unwrap();
        assert!(!s.doc.passthrough.contains_key("word/signature.bin"));
        assert!(String::from_utf8_lossy(s.doc.passthrough.get("wordcraft:vbaProject.related").unwrap()).contains("wordVbaData"));
        s.run("edit.undo", &json!({})).unwrap();
        assert_eq!(s.doc.passthrough.get("word/vbaProject.bin").unwrap().as_slice(), old);
        assert!(s.doc.passthrough.contains_key("word/signature.bin"));
    }
}
