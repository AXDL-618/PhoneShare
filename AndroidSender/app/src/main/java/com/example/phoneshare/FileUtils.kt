package com.example.phoneshare

import android.content.Context
import android.database.Cursor
import android.net.Uri
import android.provider.OpenableColumns

object FileUtils {
    fun displayName(context: Context, uri: Uri): String {
        val resolver = context.contentResolver
        var name: String? = null
        if (uri.scheme == "content") {
            var cursor: Cursor? = null
            try {
                cursor = resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)
                if (cursor != null && cursor.moveToFirst()) {
                    val index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    if (index >= 0) name = cursor.getString(index)
                }
            } finally {
                cursor?.close()
            }
        }
        if (name.isNullOrBlank()) name = uri.lastPathSegment ?: "file.bin"
        return sanitizeFileName(name!!)
    }

    fun size(context: Context, uri: Uri): Long {
        val resolver = context.contentResolver
        if (uri.scheme == "content") {
            var cursor: Cursor? = null
            try {
                cursor = resolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)
                if (cursor != null && cursor.moveToFirst()) {
                    val index = cursor.getColumnIndex(OpenableColumns.SIZE)
                    if (index >= 0 && !cursor.isNull(index)) return cursor.getLong(index)
                }
            } finally {
                cursor?.close()
            }
        }
        return -1L
    }

    fun mimeType(context: Context, uri: Uri): String {
        return context.contentResolver.getType(uri) ?: "application/octet-stream"
    }

    private fun sanitizeFileName(input: String): String {
        val invalid = charArrayOf('\\', '/', ':', '*', '?', '"', '<', '>', '|')
        var s = input.trim()
        invalid.forEach { s = s.replace(it, '_') }
        s = s.replace("..", "_")
        return if (s.isBlank()) "file.bin" else s
    }
}
