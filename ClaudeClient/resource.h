#pragma once

// Dialog IDs
#define IDD_SETUP               101
#define IDD_SETTINGS            102
#define IDD_RUNCMD              103
#define IDD_ABOUT               104
#define IDD_MODELMANAGER        105

// Main Window Controls
#define IDC_CHAT_HISTORY        1001
#define IDC_INPUT               1002
#define IDC_SEND                1003
#define IDC_STATUS              1004

// Setup / Settings Controls
#define IDC_API_KEY             1010
#define IDC_MODEL               1011
#define IDC_MAX_TOKENS          1012
#define IDC_TEMPERATURE         1013
#define IDC_SYSTEM_PROMPT       1014
#define IDC_TEST_CONN           1015
#define IDC_SAVE                1016
#define IDC_SETUP_STATUS        1017

// Server Mode Controls
#define IDC_MODE_CLOUD          1018
#define IDC_MODE_LOCAL          1019
#define IDC_LOCAL_ENDPOINT      1020
#define IDC_REFRESH_MODELS      1025

// Model Manager
#define IDC_MODEL_LIST          1030
#define IDC_HF_REPO             1031
#define IDC_HF_QUANT            1032
#define IDC_HF_PULL             1033
#define IDC_GGUF_PATH           1034
#define IDC_GGUF_BROWSE         1035
#define IDC_GGUF_NAME           1036
#define IDC_GGUF_IMPORT         1037
#define IDC_MODEL_DELETE        1038
#define IDC_MODEL_STATUS        1039
#define IDC_OLLAMA_NAME         1040
#define IDC_OLLAMA_PULL         1041

// File Browser
#define IDC_FILE_TREE           1050
#define IDC_FILE_CONTENT        1051
#define IDC_FILE_PATH           1052
#define IDC_FILE_SAVE           1053
#define IDC_FILE_OPEN_BTN       1054

// Code Search
#define IDC_SEARCH_PATTERN      1060
#define IDC_SEARCH_DIR          1061
#define IDC_SEARCH_RESULTS      1062
#define IDC_SEARCH_GO           1063

// Git
#define IDC_GIT_OUTPUT          1070
#define IDC_GIT_STATUS          1071
#define IDC_GIT_LOG             1072
#define IDC_GIT_DIFF            1073
#define IDC_GIT_COMMIT          1074
#define IDC_GIT_COMMIT_MSG      1075
#define IDC_GIT_BRANCH          1076

// Session History
#define IDC_SESSION_LIST        1080
#define IDC_SESSION_LOAD        1081
#define IDC_SESSION_DELETE      1082
#define IDC_SESSION_PREVIEW     1083

// Plugin Manager
#define IDC_PLUGIN_LIST         1090
#define IDC_PLUGIN_ENABLE       1091
#define IDC_PLUGIN_DISABLE      1092
#define IDC_PLUGIN_ADD          1093
#define IDC_PLUGIN_INFO         1094

// Web Fetch
#define IDC_WEB_URL             1100
#define IDC_WEB_CONTENT         1101
#define IDC_WEB_FETCH_BTN       1102
#define IDC_WEB_SEND_CHAT       1103

// Permissions
#define IDC_PERM_LIST           1110
#define IDC_PERM_ADD            1111
#define IDC_PERM_DENY           1112
#define IDC_PERM_REMOVE         1113
#define IDC_PERM_RULE           1114
#define IDC_PERM_MODE           1115

// Task Manager
#define IDC_TASK_LIST           1120
#define IDC_TASK_NEW            1121
#define IDC_TASK_STOP           1122
#define IDC_TASK_OUTPUT         1123
#define IDC_TASK_STATUS         1124

// Project Context
#define IDC_CTX_INFO            1130
#define IDC_CTX_REFRESH         1131
#define IDC_CTX_SEND            1132

// Voice
#define IDC_VOICE_BTN           1140

// Sidebar
#define IDC_SIDEBAR_NEWCHAT     1160
#define IDC_SIDEBAR_NAV         1170  // 1170-1179 reserved for nav items

// Run Command Dialog
#define IDC_CMD_INPUT           1150
#define IDC_CMD_OUTPUT          1151
#define IDC_CMD_RUN             1152

// Menu IDs
#define IDM_FILE_CLEAR          2001
#define IDM_FILE_EXPORT         2002
#define IDM_FILE_EXIT           2003
#define IDM_SETTINGS            2004
#define IDM_TOOLS_RUNCMD        2005
#define IDM_TOOLS_OPENFILE      2006
#define IDM_HELP_ABOUT          2007
#define IDM_TOOLS_MODELS        2008
#define IDM_TOOLS_FILEBROWSER   2010
#define IDM_TOOLS_CODESEARCH    2011
#define IDM_TOOLS_GIT           2012
#define IDM_TOOLS_WEBFETCH      2013
#define IDM_TOOLS_PLUGINS       2014
#define IDM_TOOLS_PERMISSIONS   2015
#define IDM_TOOLS_TASKS         2016
#define IDM_TOOLS_PROJECTCTX    2017
#define IDM_SESSION_SAVE        2020
#define IDM_SESSION_LOAD        2021
#define IDM_TOOLS_VOICE         2023
#define IDM_TOOLS_VIM           2024
